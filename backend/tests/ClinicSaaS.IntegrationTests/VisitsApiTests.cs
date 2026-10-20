using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

/// <summary>T-006: visits API — linkage rules, clinical-content permissions, tenant and branch isolation, concurrency.</summary>
[Collection(ApiCollection.Name)]
public sealed class VisitsApiTests(ApiFactory factory)
{
    private static void Absent(JsonElement e, string name) =>
        (e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null).Should().BeFalse($"{name} must not be returned");

    // ------------------------------------------------------------------ happy paths

    [Fact]
    public async Task Doctor_records_a_visit_for_a_checked_in_appointment_and_reads_it_back()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);

        var visit = await c.Owner.Client.RecordVisitAsync(c, appointmentId, ClinicalScenarios.Clinical());
        visit.GetProperty("appointmentId").GetGuid().Should().Be(appointmentId);
        visit.GetProperty("branchId").GetGuid().Should().Be(c.BranchId); // taken from the appointment
        visit.GetProperty("patientMrn").GetString().Should().Be("P-000001");
        visit.GetProperty("doctorName").GetString().Should().Be("د. سارة القحطاني");
        var clinical = visit.GetProperty("clinical");
        clinical.GetProperty("diagnosisCode").GetString().Should().Be("J02.9");
        clinical.GetProperty("vitals").GetProperty("temperatureC").GetDecimal().Should().Be(38.2m);

        var id = visit.GetProperty("id").GetGuid();
        var get = await c.Owner.Client.GetAsync($"/api/v1/visits/{id}");
        await get.EnsureStatusAsync(200);
        (await get.ReadJsonAsync()).GetProperty("data").GetProperty("clinical").GetProperty("diagnosis").GetString()
            .Should().Be("التهاب البلعوم الحاد");

        var list = await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(1);
        list.GetProperty("data")[0].GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");

        var byAppointment = await (await c.Owner.Client.GetAsync($"/api/v1/visits?appointmentId={appointmentId}")).ReadJsonAsync();
        byAppointment.GetProperty("data")[0].GetProperty("id").GetGuid().Should().Be(id);
    }

    [Fact]
    public async Task A_visit_without_an_appointment_is_allowed_and_defaults_to_the_only_branch()
    {
        var c = await factory.BookableClinicAsync();
        var res = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId });
        await res.EnsureStatusAsync(201);
        var v = (await res.ReadJsonAsync()).GetProperty("data");
        v.GetProperty("branchId").GetGuid().Should().Be(c.BranchId);
        Absent(v, "appointmentId");
        v.GetProperty("visitedAtUtc").GetDateTime().ToUniversalTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Clinical_content_and_follow_up_can_be_updated_with_optimistic_concurrency()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var url = $"/api/v1/visits/{visit.GetProperty("id").GetGuid()}";
        var version = visit.GetProperty("rowVersion").GetString();

        var update = await c.Owner.Client.PutAsJsonAsync(url, new
        {
            clinical = ClinicalScenarios.Clinical("التهاب اللوزتين"), followUpDate = Scenarios.LocalDateIso(7), rowVersion = version,
        });
        await update.EnsureStatusAsync(200);
        (await update.ReadJsonAsync()).GetProperty("data").GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("التهاب اللوزتين");

        var stale = await c.Owner.Client.PutAsJsonAsync(url, new { followUpNotes = "تعديل متأخر", rowVersion = version });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.ErrorCodeAsync()).Should().Be("CONCURRENCY_CONFLICT");
    }

    // ------------------------------------------------------------------ linkage rules

    [Fact]
    public async Task Only_one_visit_per_appointment()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        await c.Owner.Client.RecordVisitAsync(c, appointmentId);

        var again = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId, appointmentId });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.ErrorCodeAsync()).Should().Be("VISIT_ALREADY_RECORDED");
    }

    [Fact]
    public async Task Concurrent_visits_for_one_appointment_yield_exactly_one()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId, appointmentId })));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(5);
        foreach (var r in results.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            (await r.ErrorCodeAsync()).Should().Be("VISIT_ALREADY_RECORDED");
    }

    [Fact]
    public async Task Appointment_must_be_checked_in_first()
    {
        var c = await factory.BookableClinicAsync();
        var booked = await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(1, 11));
        var appointmentId = (await booked.ReadJsonAsync()).GetProperty("data").GetProperty("id").GetGuid();

        var res = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId, appointmentId });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await res.ErrorCodeAsync()).Should().Be("APPOINTMENT_NOT_CHECKED_IN");
    }

    [Fact]
    public async Task Visit_must_match_the_appointments_patient_doctor_and_branch()
    {
        var c = await factory.BookableClinicAsync();
        var appointmentId = await c.Owner.Client.CheckedInAppointmentAsync(c);
        var otherPatient = (await c.Owner.Client.CreatePatientAsync()).GetProperty("id").GetGuid();
        var secondBranch = await c.Owner.AddBranchAsync();

        var wrongPatient = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new { patientId = otherPatient, doctorId = c.DoctorId, appointmentId });
        wrongPatient.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongPatient.ReadJsonAsync()).GetProperty("errors").TryGetProperty("appointmentId", out _).Should().BeTrue();

        var wrongBranch = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new
        { patientId = c.PatientId, doctorId = c.DoctorId, appointmentId, branchId = secondBranch });
        wrongBranch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongBranch.ReadJsonAsync()).GetProperty("errors").TryGetProperty("branchId", out _).Should().BeTrue();

        // A second doctor of the same clinic cannot claim the first doctor's appointment either.
        var (second, doctorMembership, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor");
        var secondDoctor = (await c.Owner.Client.CreateDoctorAsync(doctorMembership, Scenarios.Week(c.BranchId))).GetProperty("id").GetGuid();
        var wrongDoctor = await second.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = secondDoctor, appointmentId });
        wrongDoctor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongDoctor.ReadJsonAsync()).GetProperty("errors").TryGetProperty("appointmentId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_it_reaches_the_database()
    {
        var c = await factory.BookableClinicAsync();
        var future = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new
        { patientId = c.PatientId, doctorId = c.DoctorId, visitedAtUtc = DateTime.UtcNow.AddDays(1) });
        future.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await future.ReadJsonAsync()).GetProperty("errors").TryGetProperty("visitedAtUtc", out _).Should().BeTrue();

        // CK_Visits_Bp would reject 400/20 — the validator answers with a 400 first.
        var badVitals = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new
        { patientId = c.PatientId, doctorId = c.DoctorId, clinical = new { vitals = new { systolicBp = 400, oxygenSaturation = 20 } } });
        badVitals.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badVitals.ErrorCodeAsync()).Should().Be("VALIDATION_FAILED");

        var badCode = await c.Owner.Client.PostAsJsonAsync("/api/v1/visits", new
        { patientId = c.PatientId, doctorId = c.DoctorId, clinical = new { diagnosisCode = "not-a-code" } });
        badCode.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Receptionist_cannot_read_or_record_visits()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");

        (await reception.GetAsync($"/api/v1/visits/{visit.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.GetAsync("/api/v1/visits")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Nurse_reads_clinical_content_but_cannot_record_or_edit_visits()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var url = $"/api/v1/visits/{visit.GetProperty("id").GetGuid()}";
        var (nurse, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");

        var get = await nurse.GetAsync(url);
        await get.EnsureStatusAsync(200);
        (await get.ReadJsonAsync()).GetProperty("data").GetProperty("clinical").ValueKind.Should().Be(JsonValueKind.Object);

        (await nurse.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId = c.DoctorId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await nurse.PutAsJsonAsync(url, new { followUpNotes = "x", rowVersion = visit.GetProperty("rowVersion").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_MedicalRecords_View_the_clinical_content_is_withheld()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var member = await factory.MemberWithPermissionsAsync(c.Owner, "Visits.View");

        var get = await member.GetAsync($"/api/v1/visits/{visit.GetProperty("id").GetGuid()}");
        await get.EnsureStatusAsync(200);
        var data = (await get.ReadJsonAsync()).GetProperty("data");
        Absent(data, "clinical"); // the API omits null members
        data.GetProperty("followUpNotes").GetString().Should().Be("مراجعة بعد أسبوعين"); // administrative: visible
        (await get.Content.ReadAsStringAsync()).Should().NotContain("التهاب").And.NotContain("J02.9");

        var list = await (await member.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).ReadJsonAsync();
        Absent(list.GetProperty("data")[0], "diagnosis");
    }

    [Fact]
    public async Task Writing_clinical_content_requires_MedicalRecords_Edit_and_omitting_it_never_wipes_it()
    {
        var c = await factory.BookableClinicAsync();
        var member = await factory.MemberWithPermissionsAsync(c.Owner, "Visits.View", "Visits.Create", "Visits.Edit");

        var withClinical = await member.PostAsJsonAsync("/api/v1/visits", new
        { patientId = c.PatientId, doctorId = c.DoctorId, clinical = ClinicalScenarios.Clinical() });
        withClinical.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The owner (the clinic's doctor) documents the visit. The member writes nothing on it — not the clinical
        // content (no MedicalRecords.Edit) and, since GD-01, not the administrative fields either (not its doctor).
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var url = $"/api/v1/visits/{visit.GetProperty("id").GetGuid()}";
        var rv = visit.GetProperty("rowVersion").GetString();
        (await member.PutAsJsonAsync(url, new { clinical = ClinicalScenarios.Clinical("x"), rowVersion = rv }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var adminOnly = await member.PutAsJsonAsync(url, new { followUpNotes = "اتصال للتذكير", rowVersion = rv });
        (await adminOnly.ErrorCodeAsync()).Should().Be("VISIT_NOT_TREATING_DOCTOR");

        // The treating doctor's update without a clinical block never wipes the clinical content.
        await (await c.Owner.Client.PutAsJsonAsync(url, new { followUpNotes = "اتصال للتذكير", rowVersion = rv })).EnsureStatusAsync(200);

        var after = (await (await c.Owner.Client.GetAsync(url)).ReadJsonAsync()).GetProperty("data");
        after.GetProperty("followUpNotes").GetString().Should().Be("اتصال للتذكير");
        after.GetProperty("clinical").GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");
    }

    [Fact]
    public async Task Audit_log_records_who_touched_a_visit_but_never_its_clinical_content()
    {
        var c = await factory.BookableClinicAsync();
        var visit = await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c), ClinicalScenarios.Clinical());
        var id = visit.GetProperty("id").GetGuid().ToString();
        await (await c.Owner.Client.GetAsync($"/api/v1/visits/{id}")).EnsureStatusAsync(200);

        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            var rows = await db.AuditLogs.Where(a => a.EntityId == id).ToListAsync();
            rows.Select(r => r.Action).Should().Contain(["visit.recorded", "visit.viewed"]);
            rows.Should().AllSatisfy(r => (r.Details ?? "").Should().NotContain("التهاب").And.NotContain("J02"));
        });
    }

    // ------------------------------------------------------------------ tenant isolation

    [Fact]
    public async Task Tenant_B_cannot_read_list_or_reference_tenant_A_visits()
    {
        var a = await factory.BookableClinicAsync();
        var appointmentOfA = await a.Owner.Client.CheckedInAppointmentAsync(a);
        var visitOfA = (await a.Owner.Client.RecordVisitAsync(a, appointmentOfA, ClinicalScenarios.Clinical())).GetProperty("id").GetGuid();
        var b = await factory.BookableClinicAsync();
        var client = b.Owner.Client;

        (await client.GetAsync($"/api/v1/visits/{visitOfA}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await client.GetAsync($"/api/v1/visits/{visitOfA}")).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
        (await client.PutAsJsonAsync($"/api/v1/visits/{visitOfA}", new { followUpNotes = "x", rowVersion = "AAAAAAAAAAA=" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/v1/patients/{a.PatientId}/visits")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var filtered = await (await client.GetAsync($"/api/v1/visits?patientId={a.PatientId}")).ReadJsonAsync();
        filtered.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);

        var withPatientOfA = await client.PostAsJsonAsync("/api/v1/visits", new { patientId = a.PatientId, doctorId = b.DoctorId });
        (await withPatientOfA.ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
        var withDoctorOfA = await client.PostAsJsonAsync("/api/v1/visits", new { patientId = b.PatientId, doctorId = a.DoctorId });
        (await withDoctorOfA.ErrorCodeAsync()).Should().Be("DOCTOR_NOT_FOUND");
        var withAppointmentOfA = await client.PostAsJsonAsync("/api/v1/visits", new
        { patientId = b.PatientId, doctorId = b.DoctorId, appointmentId = appointmentOfA });
        (await withAppointmentOfA.ErrorCodeAsync()).Should().Be("APPOINTMENT_NOT_FOUND");
        var withBranchOfA = await client.PostAsJsonAsync("/api/v1/visits", new { patientId = b.PatientId, doctorId = b.DoctorId, branchId = a.BranchId });
        (await withBranchOfA.ErrorCodeAsync()).Should().Be("BRANCH_NOT_FOUND");

        // A's visit is untouched.
        var stillThere = await a.Owner.Client.GetAsync($"/api/v1/visits/{visitOfA}");
        (await stillThere.ReadJsonAsync()).GetProperty("data").GetProperty("followUpNotes").GetString().Should().Be("مراجعة بعد أسبوعين");
    }

    // ------------------------------------------------------------------ branch isolation

    [Fact]
    public async Task Branch_restricted_doctor_only_sees_and_records_visits_of_their_branch()
    {
        var c = await factory.BookableClinicAsync();
        var secondBranch = await c.Owner.AddBranchAsync();
        // A second doctor restricted to the second branch, who also works in the main branch (so a main-branch
        // appointment can exist for them). Only they may record their own visits (GD-01).
        var (doctor, seat, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor", [secondBranch]);
        var windows = ((IEnumerable<object>)Scenarios.Week(c.BranchId, "08:00:00", "14:00:00"))
            .Concat((IEnumerable<object>)Scenarios.Week(secondBranch, "15:00:00", "22:00:00")).ToArray();
        var doctorId = (await c.Owner.Client.CreateDoctorAsync(seat, windows)).GetProperty("id").GetGuid();
        var asDoctor = c with { DoctorId = doctorId };

        // Their visit in the main branch (fixture: e.g. recorded before the branch restriction was set).
        var visitInMain = Guid.NewGuid();
        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            db.Visits.Add(new ClinicSaaS.Domain.Clinical.Visit
            {
                Id = visitInMain, TenantId = c.Owner.TenantId, BranchId = c.BranchId, PatientId = c.PatientId, DoctorId = doctorId,
                VisitedAtUtc = DateTime.UtcNow.AddDays(-1),
            });
            await db.SaveChangesAsync();
        });
        var appointmentInMain = await c.Owner.Client.CheckedInAppointmentAsync(asDoctor, hour: 12);

        (await doctor.GetAsync($"/api/v1/visits/{visitInMain}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var inMain = await doctor.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId, branchId = c.BranchId });
        (await inMain.ErrorCodeAsync()).Should().Be("BRANCH_ACCESS_DENIED");
        var forMainAppointment = await doctor.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId, appointmentId = appointmentInMain });
        (await forMainAppointment.ErrorCodeAsync()).Should().Be("APPOINTMENT_NOT_FOUND");

        // Without a branchId the restricted member's single branch is used.
        var defaulted = await doctor.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId });
        await defaulted.EnsureStatusAsync(201);
        (await defaulted.ReadJsonAsync()).GetProperty("data").GetProperty("branchId").GetGuid().Should().Be(secondBranch);

        var list = await (await doctor.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(1);
        list.GetProperty("data")[0].GetProperty("branchId").GetGuid().Should().Be(secondBranch);
    }
}
