using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests.Qa;

/// <summary>
/// GET /follow-ups (MVP acceptance decision): reception may see follow-ups due — patient, date, doctor, booked or
/// not — and nothing clinical. Same isolation as visits: tenant, branch restriction, own-doctor scope.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FollowUpListTests(ApiFactory factory)
{
    private const string SecretDiagnosis = "تشخيص سري لا يظهر للاستقبال";

    /// <summary>A visit by the clinic's doctor (the owner) in <paramref name="branchId"/> with a follow-up date and clinical content.</summary>
    private static async Task<(Guid PatientId, string Name)> FollowUpAsync(QaScenarios.TwoBranchClinic t, Guid branchId, int followUpInDays, string name)
    {
        var patient = await t.OwnerClient.CreatePatientAsync(new { fullName = name, mobileNumber = "0551234567", gender = "Male", identityType = "None" });
        var id = patient.GetProperty("id").GetGuid();
        await (await t.OwnerClient.PostAsJsonAsync("/api/v1/visits", new
        {
            patientId = id, doctorId = t.DoctorId, branchId,
            clinical = new { diagnosis = SecretDiagnosis, symptoms = "أعراض", treatmentPlan = "خطة", clinicalNotes = "ملاحظات" },
            followUpDate = Scenarios.LocalDateIso(followUpInDays), followUpNotes = "ملاحظة المتابعة",
        })).EnsureStatusAsync(201);
        return (id, name);
    }

    private static async Task<JsonElement> ListAsync(HttpClient client, string query = "")
    {
        var res = await client.GetAsync($"/api/v1/follow-ups{query}");
        await res.EnsureStatusAsync(200);
        return await res.ReadJsonAsync();
    }

    [Fact]
    public async Task Receptionist_sees_due_follow_ups_with_operational_fields_only()
    {
        var t = await factory.TwoBranchClinicAsync();
        var due = await FollowUpAsync(t, t.MainBranchId, 3, "متابعة قريبة");
        await FollowUpAsync(t, t.MainBranchId, 30, "متابعة بعيدة");
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist");

        var body = await ListAsync(reception);
        var items = body.GetProperty("data").EnumerateArray().ToList();
        items.Should().ContainSingle("the default window is today … today + 7 days");
        var item = items[0];
        item.GetProperty("patientId").GetGuid().Should().Be(due.PatientId);
        item.GetProperty("patientName").GetString().Should().Be(due.Name);
        item.GetProperty("followUpDate").GetString().Should().Be(Scenarios.LocalDateIso(3));
        item.GetProperty("doctorId").GetGuid().Should().Be(t.DoctorId);
        item.GetProperty("booked").GetBoolean().Should().BeFalse();

        // Only operational fields: no visit id and nothing clinical, under any name.
        item.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(
            ["patientId", "patientName", "patientMrn", "followUpDate", "doctorId", "doctorName", "branchId", "booked", "nextAppointmentUtc"]);
        var raw = body.GetRawText();
        raw.Should().NotContain(SecretDiagnosis).And.NotContain("ملاحظة المتابعة").And.NotContain("خطة").And.NotContain("أعراض");

        // A wider window includes the later one.
        (await ListAsync(reception, $"?from={Scenarios.LocalDateIso(0)}&to={Scenarios.LocalDateIso(40)}"))
            .GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Booking_a_later_appointment_marks_the_follow_up_booked()
    {
        var t = await factory.TwoBranchClinicAsync();
        var due = await FollowUpAsync(t, t.MainBranchId, 2, "مريض المتابعة");
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist");
        (await ListAsync(reception, "?booked=false")).GetProperty("data").GetArrayLength().Should().Be(1);

        var start = Scenarios.RiyadhUtc(2, 10);
        await (await reception.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = due.PatientId, doctorId = t.DoctorId, branchId = t.MainBranchId, startUtc = start, durationMinutes = 15, source = "Phone",
        })).EnsureStatusAsync(201);

        var item = (await ListAsync(reception)).GetProperty("data")[0];
        item.GetProperty("booked").GetBoolean().Should().BeTrue();
        item.GetProperty("nextAppointmentUtc").GetDateTime().Should().Be(start);
        (await ListAsync(reception, "?booked=false")).GetProperty("data").GetArrayLength().Should().Be(0);
        (await ListAsync(reception, "?booked=true")).GetProperty("data").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Another_clinic_sees_nothing_even_with_this_clinics_ids()
    {
        var t = await factory.TwoBranchClinicAsync();
        await FollowUpAsync(t, t.MainBranchId, 1, "متابعة العيادة أ");
        var other = await factory.BookableClinicAsync();

        (await ListAsync(other.Owner.Client)).GetProperty("data").GetArrayLength().Should().Be(0);
        (await ListAsync(other.Owner.Client, $"?doctorId={t.DoctorId}&branchId={t.MainBranchId}")).GetProperty("data").GetArrayLength().Should().Be(0);
        (await ListAsync(t.OwnerClient, $"?doctorId={Guid.NewGuid()}")).GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_branch_restricted_receptionist_sees_only_their_branch()
    {
        var t = await factory.TwoBranchClinicAsync();
        await FollowUpAsync(t, t.MainBranchId, 1, "متابعة الفرع الرئيسي");
        var north = await FollowUpAsync(t, t.SecondBranchId, 1, "متابعة فرع الشمال");
        var (reception, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "receptionist", [t.SecondBranchId]);

        var items = (await ListAsync(reception)).GetProperty("data").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("patientId").GetGuid()).Should().Equal(north.PatientId);
        (await ListAsync(reception, $"?branchId={t.MainBranchId}")).GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_doctor_without_ViewAllDoctors_sees_only_own_follow_ups_and_members_without_Appointments_View_are_refused()
    {
        var t = await factory.TwoBranchClinicAsync();
        await FollowUpAsync(t, t.MainBranchId, 1, "متابعة الطبيب المالك");
        var (doctor, membershipId, _) = await factory.InviteAndAcceptAsync(t.Owner, "doctor");
        await t.OwnerClient.CreateDoctorAsync(membershipId, Scenarios.Week(t.MainBranchId));
        (await ListAsync(doctor)).GetProperty("data").GetArrayLength().Should().Be(0, "another doctor's patients are outside own-doctor scope");
        (await ListAsync(t.OwnerClient)).GetProperty("data").GetArrayLength().Should().Be(1);

        var (accountant, _, _) = await factory.InviteAndAcceptAsync(t.Owner, "accountant"); // no Appointments.View
        (await accountant.GetAsync("/api/v1/follow-ups")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync("/api/v1/follow-ups")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_inverted_or_too_wide_window_is_a_validation_error()
    {
        var t = await factory.TwoBranchClinicAsync();
        (await t.OwnerClient.GetAsync($"/api/v1/follow-ups?from={Scenarios.LocalDateIso(5)}&to={Scenarios.LocalDateIso(1)}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await t.OwnerClient.GetAsync($"/api/v1/follow-ups?from={Scenarios.LocalDateIso(0)}&to={Scenarios.LocalDateIso(200)}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
