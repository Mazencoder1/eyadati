using System.Net;
using System.Net.Http.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// The server rules the appointment rescheduling UI relies on (the UI only offers the action and lists the
/// server's free slots): an authorized receptionist moves the same appointment to a slot from the availability
/// endpoint; a member without Appointments.Edit, a walk-in, another clinic and an unknown id are all refused.
/// Branch restriction and overlap/concurrency are covered by QaBranchIsolationTests and AppointmentTests.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RescheduleUiContractTests(ApiFactory factory)
{
    [Fact]
    public async Task Receptionist_moves_the_same_appointment_to_a_slot_offered_by_the_availability_endpoint()
    {
        var c = await factory.BookableClinicAsync();
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");
        var appt = await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10))).DataAsync(201);
        var id = appt.GetProperty("id").GetGuid();

        var slots = await (await reception.GetAsync(
            $"/api/v1/doctors/{c.DoctorId}/availability?date={Scenarios.LocalDateIso(2)}&branchId={c.BranchId}")).DataAsync(200);
        var target = slots.EnumerateArray().First(s => s.GetProperty("startUtc").GetDateTime() == Scenarios.RiyadhUtc(2, 11));

        var moved = await (await reception.PutAsJsonAsync($"/api/v1/appointments/{id}/reschedule", new
        {
            startUtc = target.GetProperty("startUtc").GetDateTime(),
            rowVersion = appt.GetProperty("rowVersion").GetString(),
        })).DataAsync(200);

        moved.GetProperty("id").GetGuid().Should().Be(id);
        moved.GetProperty("startUtc").GetDateTime().Should().Be(Scenarios.RiyadhUtc(2, 11));
        moved.GetProperty("status").GetString().Should().Be("Scheduled");
        moved.GetProperty("doctorId").GetGuid().Should().Be(c.DoctorId);
        moved.GetProperty("branchId").GetGuid().Should().Be(c.BranchId);

        var list = await (await c.Owner.Client.GetAsync(
            $"/api/v1/appointments?patientId={c.PatientId}&date={Scenarios.LocalDateIso(2)}")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(1, "rescheduling moves the appointment, it never duplicates it");
    }

    [Fact]
    public async Task Member_without_Appointments_Edit_cannot_reschedule()
    {
        var c = await factory.BookableClinicAsync();
        var (staff, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "staff"); // Appointments.View only
        var appt = await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10))).DataAsync(201);
        var url = $"/api/v1/appointments/{appt.GetProperty("id").GetGuid()}";

        (await staff.PutAsJsonAsync($"{url}/reschedule", new { startUtc = Scenarios.RiyadhUtc(2, 11), rowVersion = appt.GetProperty("rowVersion").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var unchanged = await (await c.Owner.Client.GetAsync(url)).DataAsync(200);
        unchanged.GetProperty("startUtc").GetDateTime().Should().Be(Scenarios.RiyadhUtc(2, 10));
    }

    [Fact]
    public async Task Walk_in_cannot_be_rescheduled()
    {
        var c = await factory.BookableClinicAsync();
        var walkIn = await (await c.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = c.PatientId, doctorId = c.DoctorId, branchId = c.BranchId, source = "WalkIn",
        })).DataAsync(201);

        var res = await c.Owner.Client.PutAsJsonAsync($"/api/v1/appointments/{walkIn.GetProperty("id").GetGuid()}/reschedule",
            new { startUtc = Scenarios.RiyadhUtc(2, 10), rowVersion = walkIn.GetProperty("rowVersion").GetString() });

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ErrorCodeAsync()).Should().Be("APPOINTMENT_NOT_EDITABLE");
    }

    [Fact]
    public async Task Another_clinic_or_an_unknown_id_gets_404_and_nothing_changes()
    {
        var c = await factory.BookableClinicAsync();
        var other = await factory.BookableClinicAsync();
        var appt = await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(2, 10))).DataAsync(201);
        var url = $"/api/v1/appointments/{appt.GetProperty("id").GetGuid()}";
        var body = new { startUtc = Scenarios.RiyadhUtc(2, 11), rowVersion = appt.GetProperty("rowVersion").GetString() };

        (await other.Owner.Client.PutAsJsonAsync($"{url}/reschedule", body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.Owner.Client.PutAsJsonAsync($"/api/v1/appointments/{Guid.NewGuid()}/reschedule", body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unchanged = await (await c.Owner.Client.GetAsync(url)).DataAsync(200);
        unchanged.GetProperty("startUtc").GetDateTime().Should().Be(Scenarios.RiyadhUtc(2, 10));
        unchanged.GetProperty("rowVersion").GetString().Should().Be(appt.GetProperty("rowVersion").GetString());
    }
}
