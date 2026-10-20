using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

[Collection(ApiCollection.Name)]
public sealed class AppointmentTests(ApiFactory factory)
{
    [Fact]
    public async Task Booking_inside_working_hours_succeeds_and_returns_display_names()
    {
        var c = await factory.BookableClinicAsync();
        var res = await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10));
        await res.EnsureStatusAsync(201);
        var a = (await res.ReadJsonAsync()).GetProperty("data");
        a.GetProperty("status").GetString().Should().Be("Scheduled");
        a.GetProperty("doctorName").GetString().Should().Be("د. سارة القحطاني");
        a.GetProperty("patientMrn").GetString().Should().Be("P-000001");
        (a.GetProperty("endUtc").GetDateTime() - a.GetProperty("startUtc").GetDateTime()).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Booking_outside_working_hours_in_clinic_time_is_rejected()
    {
        var c = await factory.BookableClinicAsync();
        // 03:00 Riyadh — the doctor works 08:00–22:00 clinic time (UTC would wrongly say 00:00).
        var res = await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 3));
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ErrorCodeAsync()).Should().Be("OUTSIDE_WORKING_HOURS");

        // Ending after the window closes is rejected too.
        (await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 21, 50), 15)).ErrorCodeAsync()).Should().Be("OUTSIDE_WORKING_HOURS");
    }

    [Fact]
    public async Task Overlaps_are_rejected_but_touching_edges_are_allowed()
    {
        var c = await factory.BookableClinicAsync();
        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10), 30)).EnsureStatusAsync(201);

        var overlap = await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10, 15), 15);
        overlap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await overlap.ErrorCodeAsync()).Should().Be("SLOT_TAKEN");

        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10, 30), 15)).EnsureStatusAsync(201); // 10:30 touches 10:00–10:30
        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 9, 45), 15)).EnsureStatusAsync(201);  // 09:45–10:00 touches too
    }

    [Fact]
    public async Task Concurrent_bookings_of_the_same_slot_yield_exactly_one_appointment()
    {
        var c = await factory.BookableClinicAsync();
        var start = Scenarios.RiyadhUtc(3, 11);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => c.Owner.Client.BookAsync(c, start)));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(7);
    }

    [Fact]
    public async Task Concurrent_overlapping_bookings_with_different_starts_yield_exactly_one()
    {
        // 10:00, 10:05 … 10:25, each 30 minutes: all pairwise overlapping, none share a start time, so the
        // unique index can't help — only the per-doctor lock around check-then-insert prevents double booking.
        var c = await factory.BookableClinicAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(3, 10, i * 5), 30)));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
    }

    [Fact]
    public async Task Past_slots_are_rejected()
    {
        var c = await factory.BookableClinicAsync();
        var res = await c.Owner.Client.BookAsync(c, DateTime.UtcNow.AddHours(-2));
        (await res.ErrorCodeAsync()).Should().BeOneOf("SLOT_IN_PAST");
    }

    [Fact]
    public async Task Walk_ins_get_daily_queue_tokens_and_may_overlap()
    {
        var c = await factory.BookableClinicAsync();
        var first = await c.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new { patientId = c.PatientId, doctorId = c.DoctorId, source = "WalkIn" });
        var second = await c.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new { patientId = c.PatientId, doctorId = c.DoctorId, source = "WalkIn" });
        await first.EnsureStatusAsync(201);
        await second.EnsureStatusAsync(201);
        (await first.ReadJsonAsync()).GetProperty("data").GetProperty("tokenNumber").GetString().Should().Be("T-01");
        (await second.ReadJsonAsync()).GetProperty("data").GetProperty("tokenNumber").GetString().Should().Be("T-02");
    }

    [Fact]
    public async Task Status_flow_enforces_transitions_and_reasons_and_frees_the_slot_on_cancel()
    {
        var c = await factory.BookableClinicAsync();
        var start = Scenarios.RiyadhUtc(2, 12);
        var a = (await (await c.Owner.Client.BookAsync(c, start)).ReadJsonAsync()).GetProperty("data");
        var id = a.GetProperty("id").GetGuid();
        var url = $"/api/v1/appointments/{id}";

        // Completing a not-yet-arrived patient skips check-in: illegal.
        var complete = await c.Owner.Client.PostAsJsonAsync($"{url}/complete", new { rowVersion = a.GetProperty("rowVersion").GetString() });
        (await complete.ErrorCodeAsync()).Should().Be("INVALID_STATUS_TRANSITION");

        var noShow = await c.Owner.Client.PostAsJsonAsync($"{url}/no-show", new { rowVersion = await c.Owner.Client.RowVersionAsync(url) });
        (await noShow.ErrorCodeAsync()).Should().Be("NO_SHOW_BEFORE_START");

        var noReason = await c.Owner.Client.PostAsJsonAsync($"{url}/cancel", new { rowVersion = await c.Owner.Client.RowVersionAsync(url) });
        (await noReason.ErrorCodeAsync()).Should().Be("CANCELLATION_REASON_REQUIRED");

        var cancel = await c.Owner.Client.PostAsJsonAsync($"{url}/cancel", new { rowVersion = await c.Owner.Client.RowVersionAsync(url), reason = "سفر المريض" });
        await cancel.EnsureStatusAsync(200);
        (await cancel.ReadJsonAsync()).GetProperty("data").GetProperty("status").GetString().Should().Be("Cancelled");

        // The cancelled slot is free again (filtered unique index ignores terminal statuses).
        await (await c.Owner.Client.BookAsync(c, start)).EnsureStatusAsync(201);
    }

    [Fact]
    public async Task Check_in_then_complete()
    {
        var c = await factory.BookableClinicAsync();
        var id = (await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 13))).ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();
        var url = $"/api/v1/appointments/{id}";
        await (await c.Owner.Client.PostAsJsonAsync($"{url}/check-in", new { rowVersion = await c.Owner.Client.RowVersionAsync(url) })).EnsureStatusAsync(200);
        var done = await c.Owner.Client.PostAsJsonAsync($"{url}/complete", new { rowVersion = await c.Owner.Client.RowVersionAsync(url) });
        (await done.ReadJsonAsync()).GetProperty("data").GetProperty("status").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task Reschedule_respects_overlap_and_optimistic_concurrency()
    {
        var c = await factory.BookableClinicAsync();
        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 15))).EnsureStatusAsync(201);
        var a = (await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 16))).ReadJsonAsync()).GetProperty("data");
        var url = $"/api/v1/appointments/{a.GetProperty("id").GetGuid()}";
        var version = a.GetProperty("rowVersion").GetString();

        var clash = await c.Owner.Client.PutAsJsonAsync($"{url}/reschedule", new { startUtc = Scenarios.RiyadhUtc(2, 15), rowVersion = version });
        (await clash.ErrorCodeAsync()).Should().Be("SLOT_TAKEN");

        await (await c.Owner.Client.PutAsJsonAsync($"{url}/reschedule", new { startUtc = Scenarios.RiyadhUtc(2, 17), rowVersion = version })).EnsureStatusAsync(200);
        var stale = await c.Owner.Client.PutAsJsonAsync($"{url}/reschedule", new { startUtc = Scenarios.RiyadhUtc(2, 18), rowVersion = version });
        (await stale.ErrorCodeAsync()).Should().Be("CONCURRENCY_CONFLICT");
    }

    [Fact]
    public async Task Availability_lists_free_slots_in_clinic_time_and_excludes_booked_ones()
    {
        var c = await factory.BookableClinicAsync();
        // Narrow the working hours to 09:00–10:00 → four 15-minute slots.
        var doctorUrl = $"/api/v1/doctors/{c.DoctorId}";
        await (await c.Owner.Client.PutAsJsonAsync($"{doctorUrl}/schedule", new
        {
            windows = Scenarios.Week(c.BranchId, "09:00:00", "10:00:00"),
            rowVersion = await c.Owner.Client.RowVersionAsync(doctorUrl),
        })).EnsureStatusAsync(200);
        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 9, 15))).EnsureStatusAsync(201);

        var date = Scenarios.LocalDateIso(2);
        var body = await (await c.Owner.Client.GetAsync($"{doctorUrl}/availability?date={date}")).ReadJsonAsync();
        body.GetProperty("data").EnumerateArray().Select(s => s.GetProperty("localTime").GetString())
            .Should().Equal("09:00", "09:30", "09:45");
    }

    [Fact]
    public async Task Daily_board_lists_the_days_appointments_in_order()
    {
        var c = await factory.BookableClinicAsync();
        await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(4, 14));
        await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(4, 9));
        await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(5, 9));

        var date = Scenarios.LocalDateIso(4);
        var body = await (await c.Owner.Client.GetAsync($"/api/v1/appointments?date={date}")).ReadJsonAsync();
        var starts = body.GetProperty("data").EnumerateArray().Select(a => a.GetProperty("startUtc").GetDateTime()).ToList();
        starts.Should().HaveCount(2).And.BeInAscendingOrder();
    }

    [Fact]
    public async Task Another_clinic_cannot_see_book_with_or_change_these_records()
    {
        var a = await factory.BookableClinicAsync();
        var b = await factory.BookableClinicAsync();
        var apptOfA = (await (await a.Owner.Client.BookAsync(a, Scenarios.RiyadhUtc(2, 10))).ReadJsonAsync()).GetProperty("data");
        var url = $"/api/v1/appointments/{apptOfA.GetProperty("id").GetGuid()}";

        (await b.Owner.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await b.Owner.Client.PostAsJsonAsync($"{url}/cancel", new { rowVersion = apptOfA.GetProperty("rowVersion").GetString(), reason = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // B tries to book with A's patient and A's doctor.
        var withPatientOfA = await b.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        { patientId = a.PatientId, doctorId = b.DoctorId, branchId = b.BranchId, startUtc = Scenarios.RiyadhUtc(2, 11) });
        (await withPatientOfA.ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
        var withDoctorOfA = await b.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        { patientId = b.PatientId, doctorId = a.DoctorId, branchId = b.BranchId, startUtc = Scenarios.RiyadhUtc(2, 11) });
        (await withDoctorOfA.ErrorCodeAsync()).Should().Be("DOCTOR_NOT_FOUND");
        (await b.Owner.Client.GetAsync($"/api/v1/doctors/{a.DoctorId}/availability?date={Scenarios.LocalDateIso(2)}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Branch_restricted_staff_only_see_their_branch()
    {
        var c = await factory.BookableClinicAsync();
        var second = await c.Owner.Client.PostAsJsonAsync("/api/v1/branches", new { name = "فرع الشمال", city = "الرياض" });
        await second.EnsureStatusAsync(201);
        var secondBranch = (await second.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        var apptInMain = (await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10))).ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist", [secondBranch]);

        (await reception.GetAsync($"/api/v1/appointments/{apptInMain}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var list = await (await reception.GetAsync($"/api/v1/appointments?date={Scenarios.LocalDateIso(2)}")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);

        var bookInMain = await reception.BookAsync(c, Scenarios.RiyadhUtc(2, 11));
        (await bookInMain.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
    }

    [Fact]
    public async Task Monthly_appointment_limit_of_the_plan_is_enforced()
    {
        var c = await factory.BookableClinicAsync();
        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            var tiny = new ClinicSaaS.Domain.Platform.Plan { Code = "tiny-a-" + Guid.NewGuid().ToString("N")[..6], NameAr = "x", NameEn = "x", IsPublic = false };
            tiny.Limits.Add(new ClinicSaaS.Domain.Platform.PlanLimit { LimitKey = "max_appointments_month", MaxValue = 1 });
            db.Plans.Add(tiny);
            (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.Subscriptions)).PlanId = tiny.Id;
            await db.SaveChangesAsync();
        });

        await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10))).EnsureStatusAsync(201);
        (await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 11))).ErrorCodeAsync()).Should().Be("USAGE_LIMIT_REACHED");
    }
}
