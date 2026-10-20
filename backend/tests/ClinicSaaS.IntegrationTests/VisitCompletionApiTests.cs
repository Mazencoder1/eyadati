using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-013b: POST /visits/{id}/complete — the explicit action that closes the visit (locking its clinical content) and,
/// if linked, the appointment (CheckedIn → Completed). Saving a visit still never completes anything.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class VisitCompletionApiTests(ApiFactory factory)
{
    private static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid visitId, string? rowVersion) =>
        client.PostAsJsonAsync($"/api/v1/visits/{visitId}/complete", new { rowVersion });

    [Fact]
    public async Task Completing_a_visit_closes_it_and_its_checked_in_appointment()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId, ClinicalScenarios.Clinical());
        visit.GetProperty("status").GetString().Should().Be("Open");
        var id = visit.GetProperty("id").GetGuid();

        var res = await CompleteAsync(c.Owner.Client, id, visit.GetProperty("rowVersion").GetString());
        var done = await res.DataAsync(200);
        done.GetProperty("status").GetString().Should().Be("Completed");
        done.GetProperty("completedAtUtc").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));
        done.GetProperty("completedByUserId").GetGuid().Should().Be(c.Owner.UserId);
        done.GetProperty("rowVersion").GetString().Should().NotBe(visit.GetProperty("rowVersion").GetString());

        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("Completed");
        var listed = (await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).DataAsync(200))[0];
        listed.GetProperty("status").GetString().Should().Be("Completed");
    }

    [Fact]
    public async Task A_visit_without_appointment_completes_alone_and_an_already_completed_appointment_is_left_as_is()
    {
        var c = await factory.BookableClinicAsync();
        var walkIn = await c.Owner.Client.RecordVisitAsync(c, appointmentId: null);
        (await (await CompleteAsync(c.Owner.Client, walkIn.GetProperty("id").GetGuid(), walkIn.GetProperty("rowVersion").GetString())).DataAsync(200))
            .GetProperty("status").GetString().Should().Be("Completed");

        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c, hour: 12);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId);
        var appointment = await (await c.Owner.Client.GetAsync($"/api/v1/appointments/{appointmentId}")).DataAsync(200);
        await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/appointments/{appointmentId}/complete",
            new { rowVersion = appointment.GetProperty("rowVersion").GetString() })).EnsureStatusAsync(200);

        await (await CompleteAsync(c.Owner.Client, visit.GetProperty("id").GetGuid(), visit.GetProperty("rowVersion").GetString())).EnsureStatusAsync(200);
        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("Completed");
    }

    [Fact]
    public async Task An_appointment_that_cannot_complete_blocks_the_whole_action_without_partial_writes()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId);
        var appointment = await (await c.Owner.Client.GetAsync($"/api/v1/appointments/{appointmentId}")).DataAsync(200);
        await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/appointments/{appointmentId}/cancel",
            new { rowVersion = appointment.GetProperty("rowVersion").GetString(), reason = "غادر المريض" })).EnsureStatusAsync(200);

        var res = await CompleteAsync(c.Owner.Client, visit.GetProperty("id").GetGuid(), visit.GetProperty("rowVersion").GetString());
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ErrorCodeAsync()).Should().Be("INVALID_STATUS_TRANSITION");

        var after = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{visit.GetProperty("id").GetGuid()}")).DataAsync(200);
        after.GetProperty("status").GetString().Should().Be("Open");
        after.GetProperty("rowVersion").GetString().Should().Be(visit.GetProperty("rowVersion").GetString());
        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("Cancelled");
    }

    [Fact]
    public async Task Completing_twice_or_with_a_stale_row_version_is_refused_and_a_completed_visit_is_locked()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var id = visit.GetProperty("id").GetGuid();
        var original = visit.GetProperty("rowVersion").GetString();

        // Stale: someone edited the visit after we loaded it.
        var edited = await (await c.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{id}", new { followUpNotes = "تعديل", rowVersion = original })).DataAsync(200);
        var stale = await CompleteAsync(c.Owner.Client, id, original);
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ErrorCodeAsync()).Should().Be("CONCURRENCY_CONFLICT");

        var done = await (await CompleteAsync(c.Owner.Client, id, edited.GetProperty("rowVersion").GetString())).DataAsync(200);
        var again = await CompleteAsync(c.Owner.Client, id, done.GetProperty("rowVersion").GetString());
        again.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await again.ErrorCodeAsync()).Should().Be("INVALID_STATUS_TRANSITION");

        // Locked: no edits after completion, clinical or administrative.
        var put = await c.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{id}", new
        {
            clinical = ClinicalScenarios.Clinical("تشخيص متأخر"), followUpNotes = "x", rowVersion = done.GetProperty("rowVersion").GetString(),
        });
        put.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await put.ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");
        var after = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{id}")).DataAsync(200);
        after.GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");
        after.GetProperty("followUpNotes").GetString().Should().Be("تعديل");

        // A completed visit is clinically locked: no new prescription on it either (MVP acceptance decision).
        var rx = await c.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, visitId = id,
            items = new[] { new { medicationName = "Paracetamol", strength = "500 mg" } },
        });
        rx.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await rx.ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");
    }

    [Fact]
    public async Task Completing_needs_Visits_Edit_and_MedicalRecords_Edit()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c));
        var id = visit.GetProperty("id").GetGuid();
        var rv = visit.GetProperty("rowVersion").GetString();

        var (nurse, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");
        var editOnly = await factory.MemberWithPermissionsAsync(c.Owner, "Visits.View", "Visits.Edit");
        foreach (var client in new[] { nurse, reception, editOnly })
            (await CompleteAsync(client, id, rv)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // GD-01: the permissions alone are not enough — a member who is not the visit's doctor is refused.
        var clinician = await factory.MemberWithPermissionsAsync(c.Owner, "Visits.View", "Visits.Edit", "MedicalRecords.Edit");
        (await (await CompleteAsync(clinician, id, rv)).ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");

        // The visit's own doctor with Visits.Edit + MedicalRecords.Edit is enough — no Appointments.CheckIn needed for
        // the appointment to close.
        var role = await c.Owner.Client.CreateRoleAsync("طبيب بصلاحيات دنيا", "Visits.View", "Visits.Create", "Visits.Edit", "MedicalRecords.Edit");
        var (doctor, seat, _) = await factory.InviteWithRolesAsync(c.Owner.Client, [role], prefix: "mindoctor");
        var own = c with { DoctorId = (await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(c.BranchId))).GetProperty("id").GetGuid() };
        var ownVisit = await doctor.RecordVisitAsync(own, await c.Owner.Client.CheckedInAppointmentAsync(own, hour: 12));
        await (await CompleteAsync(doctor, ownVisit.GetProperty("id").GetGuid(), ownVisit.GetProperty("rowVersion").GetString())).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Other_tenants_other_branches_and_other_doctors_get_not_found()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId, ClinicalScenarios.Clinical());
        var id = visit.GetProperty("id").GetGuid();
        var rv = visit.GetProperty("rowVersion").GetString();

        var other = await factory.BookableClinicAsync();
        var secondBranch = await c.Owner.AddBranchAsync();
        var (restricted, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor", [secondBranch]);
        var (_, seat, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor");
        await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(c.BranchId));
        var otherDoctor = await factory.LoginAsync((await (await c.Owner.Client.GetAsync($"/api/v1/staff/{seat}")).DataAsync(200)).GetProperty("email").GetString()!);

        foreach (var client in new[] { other.Owner.Client, restricted, otherDoctor })
        {
            var res = await CompleteAsync(client, id, rv);
            res.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await res.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        }

        var after = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{id}")).DataAsync(200);
        after.GetProperty("status").GetString().Should().Be("Open");
        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("CheckedIn");
    }

    [Fact]
    public async Task Prescriptions_racing_completion_are_never_issued_after_the_visit_is_completed()
    {
        for (var round = 0; round < 4; round++)
        {
            var c = await factory.BookableClinicAsync();
            var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
            var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId);
            var id = visit.GetProperty("id").GetGuid();

            var complete = CompleteAsync(c.Owner.Client, id, visit.GetProperty("rowVersion").GetString());
            var rxs = Enumerable.Range(0, 4).Select(_ => c.Owner.Client.IssuePrescriptionAsync(id)).ToList();
            await Task.WhenAll([complete, .. rxs]);

            (await complete).StatusCode.Should().Be(HttpStatusCode.OK);
            foreach (var r in rxs.Select(t => t.Result))
            {
                ((int)r.StatusCode).Should().BeOneOf(201, 422);
                if (r.StatusCode == HttpStatusCode.UnprocessableEntity) (await r.ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");
            }
            var done = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{id}")).DataAsync(200);
            var completedAt = done.GetProperty("completedAtUtc").GetDateTime();
            var list = await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).DataAsync(200);
            list.GetArrayLength().Should().Be(rxs.Count(t => t.Result.StatusCode == HttpStatusCode.Created));
            foreach (var p in list.EnumerateArray())
                p.GetProperty("issuedAtUtc").GetDateTime().Should().BeOnOrBefore(completedAt, $"round {round}: issued while the visit was open");
        }
    }

    [Fact]
    public async Task Concurrent_completes_yield_exactly_one_success()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId);
        var id = visit.GetProperty("id").GetGuid();
        var rv = visit.GetProperty("rowVersion").GetString();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CompleteAsync(c.Owner.Client, id, rv)));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Should()
            .OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict || r.StatusCode == HttpStatusCode.UnprocessableEntity);
        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("Completed");

        await factory.WithDbAsync(c.Owner.TenantId, async db =>
            (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "visit.completed")).Should().Be(1));
    }

    [Fact]
    public async Task Completion_is_audited_without_clinical_content()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId, ClinicalScenarios.Clinical());
        var id = visit.GetProperty("id").GetGuid();
        await (await CompleteAsync(c.Owner.Client, id, visit.GetProperty("rowVersion").GetString())).EnsureStatusAsync(200);

        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            var visitRows = await db.AuditLogs.Where(a => a.EntityId == id.ToString() && a.Action == "visit.completed").ToListAsync();
            visitRows.Should().ContainSingle();
            var appointmentRows = await db.AuditLogs
                .Where(a => a.EntityId == appointmentId.ToString() && a.Action == "appointment.status_changed").ToListAsync();
            appointmentRows.Should().Contain(r => (r.Details ?? "").Contains("Completed"));
            visitRows.Concat(appointmentRows).Should().AllSatisfy(r => (r.Details ?? "").Should().NotContain("التهاب").And.NotContain("J02"));
        });
    }

    private static async Task<string> AppointmentStatusAsync(Scenarios.BookableClinic c, Guid id) =>
        (await (await c.Owner.Client.GetAsync($"/api/v1/appointments/{id}")).DataAsync(200)).GetProperty("status").GetString()!;
}
