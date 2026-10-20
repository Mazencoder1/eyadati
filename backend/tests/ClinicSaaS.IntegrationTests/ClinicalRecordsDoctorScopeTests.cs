using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-012 (T-006 decisions): a user whose seat has a doctor profile sees and creates only their OWN visits and
/// prescriptions unless they hold MedicalRecords.ViewAllDoctors (other doctors' records are "not found"). Reading
/// prescriptions also needs MedicalRecords.View. Recording a visit never completes the appointment.
/// The BookableClinic owner is doctor A (owner role: every tenant permission, including ViewAllDoctors).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ClinicalRecordsDoctorScopeTests(ApiFactory factory)
{
    private static object[] Items() =>
        [new { medicationName = "Paracetamol", strength = "500 mg", dosage = "1 tablet", frequency = "3 times daily", route = "oral", durationDays = 5 }];

    [Fact]
    public async Task Doctor_sees_and_records_only_their_own_visits()
    {
        var c = await factory.BookableClinicAsync();
        var visitOfA = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var visitOfAId = visitOfA.GetProperty("id").GetGuid();
        var (doctorB, doctorBId) = await SecondDoctorAsync(c, "doctor");

        var own = await doctorB.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = doctorBId, clinical = ClinicalScenarios.Clinical("زكام") });
        var ownId = (await own.DataAsync(201)).GetProperty("id").GetGuid();

        // Lists: only B's own visit, whatever the filter.
        foreach (var url in new[] { "/api/v1/visits", $"/api/v1/patients/{c.PatientId}/visits", $"/api/v1/visits?doctorId={c.DoctorId}" })
        {
            var list = await (await doctorB.GetAsync(url)).ReadJsonAsync();
            list.GetProperty("data").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).Should().NotContain(visitOfAId, url);
            list.GetProperty("data").EnumerateArray().Where(v => v.GetProperty("doctorId").GetGuid() != doctorBId).Should().BeEmpty(url);
        }

        (await (await doctorB.GetAsync("/api/v1/visits")).DataAsync(200)).EnumerateArray().Select(v => v.GetProperty("id").GetGuid())
            .Should().Equal(ownId);
        await (await doctorB.GetAsync($"/api/v1/visits/{ownId}")).EnsureStatusAsync(200);

        // Another doctor's visit is "not found" (no existence leak), for reading and editing.
        var get = await doctorB.GetAsync($"/api/v1/visits/{visitOfAId}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await get.ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        (await doctorB.PutAsJsonAsync($"/api/v1/visits/{visitOfAId}", new { followUpNotes = "x", rowVersion = visitOfA.GetProperty("rowVersion").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // …and B cannot record a visit in A's name.
        var asA = await doctorB.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId });
        asA.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await asA.ErrorCodeAsync()).Should().Be("DOCTOR_NOT_FOUND");

        // The owner (ViewAllDoctors) and a nurse (no doctor profile: unchanged rules) see both.
        var (nurse, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");
        foreach (var client in new[] { c.Owner.Client, nurse })
        {
            var all = await (await client.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).ReadJsonAsync();
            all.GetProperty("data").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).Should().Contain([visitOfAId, ownId]);
        }
        var unchanged = await (await c.Owner.Client.GetAsync($"/api/v1/visits/{visitOfAId}")).DataAsync(200);
        unchanged.GetProperty("followUpNotes").GetString().Should().Be("مراجعة بعد أسبوعين");
    }

    [Fact]
    public async Task Doctor_sees_and_issues_only_their_own_prescriptions()
    {
        var c = await factory.BookableClinicAsync();
        var visitOfA = (await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c))).GetProperty("id").GetGuid();
        var rxOfA = await c.Owner.Client.PrescriptionIdAsync(visitOfA);
        var (doctorB, doctorBId) = await SecondDoctorAsync(c, "doctor");

        var ownVisit = (await (await doctorB.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = doctorBId }))
            .DataAsync(201)).GetProperty("id").GetGuid();
        var own = await doctorB.PrescriptionIdAsync(ownVisit);
        (await (await doctorB.GetAsync($"/api/v1/prescriptions/{own}")).DataAsync(200)).GetProperty("doctorId").GetGuid().Should().Be(doctorBId);

        var list = await (await doctorB.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).DataAsync(200);
        list.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).Should().Equal(own);
        var get = await doctorB.GetAsync($"/api/v1/prescriptions/{rxOfA}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await get.ErrorCodeAsync()).Should().Be("PRESCRIPTION_NOT_FOUND");

        // B cannot issue on A's visit (hidden from B ⇒ not found), even when naming A as the doctor in an old-style body.
        (await (await doctorB.IssuePrescriptionAsync(visitOfA)).ErrorCodeAsync())
            .Should().Be("VISIT_NOT_FOUND", "A's visit is not visible to B, so it cannot be linked either");
        (await (await doctorB.PostAsJsonAsync("/api/v1/prescriptions", new { visitId = visitOfA, doctorId = c.DoctorId, items = Items() })).ErrorCodeAsync())
            .Should().Be("VISIT_NOT_FOUND");

        var all = await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).DataAsync(200);
        all.GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ViewAllDoctors_lets_a_doctor_see_other_doctors_records_but_not_record_or_prescribe_on_them()
    {
        var c = await factory.BookableClinicAsync();
        var visitOfA = (await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c))).GetProperty("id").GetGuid();
        var rxOfA = await c.Owner.Client.PrescriptionIdAsync(visitOfA);

        var seniorRole = await c.Owner.Client.CreateRoleAsync("طبيب مشرف",
            "Patients.View", "MedicalRecords.View", "MedicalRecords.Edit", "MedicalRecords.ViewAllDoctors", "Doctors.View",
            "Visits.View", "Visits.Create", "Visits.Edit", "Prescriptions.View", "Prescriptions.Create");
        var (senior, seat, _) = await factory.InviteWithRolesAsync(c.Owner.Client, [seniorRole], prefix: "seniordoctor");
        await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(c.BranchId));

        await (await senior.GetAsync($"/api/v1/visits/{visitOfA}")).EnsureStatusAsync(200);
        await (await senior.GetAsync($"/api/v1/prescriptions/{rxOfA}")).EnsureStatusAsync(200);
        // GD-01: ViewAllDoctors is read-only — no visit in a colleague's name.
        var recorded = await senior.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId });
        recorded.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await recorded.ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");

        // D8: seeing a colleague's visit never makes you its prescriber.
        var res = await senior.IssuePrescriptionAsync(visitOfA);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await res.ErrorCodeAsync()).Should().Be("PRESCRIBER_NOT_TREATING_DOCTOR");
    }

    [Fact]
    public async Task Reading_prescriptions_requires_MedicalRecords_View()
    {
        var c = await factory.BookableClinicAsync();
        var rx = await c.Owner.Client.PrescriptionIdAsync(await c.Owner.Client.VisitIdAsync(c));

        var withoutMedical = await factory.MemberWithPermissionsAsync(c.Owner, "Prescriptions.View");
        (await withoutMedical.GetAsync($"/api/v1/prescriptions/{rx}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await withoutMedical.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var withMedical = await factory.MemberWithPermissionsAsync(c.Owner, "Prescriptions.View", "MedicalRecords.View");
        await (await withMedical.GetAsync($"/api/v1/prescriptions/{rx}")).EnsureStatusAsync(200);
        await (await withMedical.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).EnsureStatusAsync(200);
    }

    [Fact]
    public async Task Recording_or_editing_a_visit_never_completes_the_appointment()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId, ClinicalScenarios.Clinical());
        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("CheckedIn");

        await (await c.Owner.Client.PutAsJsonAsync($"/api/v1/visits/{visit.GetProperty("id").GetGuid()}", new
        {
            clinical = ClinicalScenarios.Clinical("تشخيص نهائي"), followUpNotes = "تم", rowVersion = visit.GetProperty("rowVersion").GetString(),
        })).EnsureStatusAsync(200);
        (await AppointmentStatusAsync(c, appointmentId)).Should().Be("CheckedIn");
    }

    [Fact]
    public async Task Role_templates_grant_ViewAllDoctors_to_owner_and_clinic_manager_only()
    {
        var c = await factory.BookableClinicAsync();
        var roles = await (await c.Owner.Client.GetAsync("/api/v1/roles")).DataAsync(200);
        List<string> Perms(string key) => roles.EnumerateArray()
            .Single(r => r.TryGetProperty("systemKey", out var k) && k.GetString() == key)
            .GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToList();

        Perms("tenant_owner").Should().Contain("MedicalRecords.ViewAllDoctors");
        Perms("clinic_manager").Should().Contain("MedicalRecords.ViewAllDoctors");
        foreach (var key in new[] { "doctor", "nurse", "receptionist", "accountant", "staff" })
            Perms(key).Should().NotContain("MedicalRecords.ViewAllDoctors", key);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A second person with the given system role and their own doctor profile (works all week at the main branch).</summary>
    private async Task<(HttpClient Client, Guid DoctorId)> SecondDoctorAsync(Scenarios.BookableClinic c, string roleKey)
    {
        var (client, seat, _) = await factory.InviteAndAcceptAsync(c.Owner, roleKey);
        var doctor = await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(c.BranchId));
        return (client, doctor.GetProperty("id").GetGuid());
    }

    private static async Task<string> AppointmentStatusAsync(Scenarios.BookableClinic c, Guid id) =>
        (await (await c.Owner.Client.GetAsync($"/api/v1/appointments/{id}")).DataAsync(200)).GetProperty("status").GetString()!;
}
