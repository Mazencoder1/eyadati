using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Phase 2 / Gate B: the API the clinical UI depends on (/auth/me doctor identity, appointment check-in time), the D6
/// patient-deletion guard, and the full reception → doctor → prescription → history flow walked by the real roles,
/// with the unauthorized paths (other doctor, receptionist, other tenant, other branch, direct ids) checked alongside.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class Phase2ClinicalWorkflowTests(ApiFactory factory)
{
    // ------------------------------------------------------------------ /auth/me

    [Fact]
    public async Task Me_returns_the_callers_own_seat_and_doctor_profile_from_the_token()
    {
        var c = await factory.BookableClinicAsync();
        var (doctor, doctorSeat, doctorId) = await TreatingDoctorAsync(c);
        var (nurse, nurseSeat, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");

        var me = await (await doctor.GetAsync("/api/v1/auth/me")).DataAsync(200);
        me.GetProperty("membershipId").GetGuid().Should().Be(doctorSeat);
        me.GetProperty("doctorId").GetGuid().Should().Be(doctorId);

        var ownerMe = await (await c.Owner.Client.GetAsync("/api/v1/auth/me")).DataAsync(200);
        ownerMe.GetProperty("membershipId").GetGuid().Should().Be(await c.Owner.OwnerMembershipIdAsync());
        ownerMe.GetProperty("doctorId").GetGuid().Should().Be(c.DoctorId, "the scenario's owner is also its doctor");

        var nurseMe = await (await nurse.GetAsync("/api/v1/auth/me")).DataAsync(200);
        nurseMe.GetProperty("membershipId").GetGuid().Should().Be(nurseSeat);
        nurseMe.TryGetProperty("doctorId", out _).Should().BeFalse("a nurse has no doctor profile (null fields are omitted)");

        // A query string cannot change who "me" is.
        var spoofed = await (await nurse.GetAsync($"/api/v1/auth/me?doctorId={doctorId}&membershipId={doctorSeat}")).DataAsync(200);
        spoofed.GetProperty("membershipId").GetGuid().Should().Be(nurseSeat);
        spoofed.TryGetProperty("doctorId", out _).Should().BeFalse();
    }

    // ------------------------------------------------------------------ appointment check-in time

    [Fact]
    public async Task Appointment_exposes_checkedInAtUtc_only_after_check_in()
    {
        var c = await factory.BookableClinicAsync();
        var booked = await (await c.Owner.Client.BookAsync(c, Scenarios.RiyadhUtc(1, 11))).DataAsync(201);
        booked.TryGetProperty("checkedInAtUtc", out _).Should().BeFalse();

        var id = booked.GetProperty("id").GetGuid();
        var checkedIn = await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/appointments/{id}/check-in",
            new { rowVersion = booked.GetProperty("rowVersion").GetString() })).DataAsync(200);
        var at = checkedIn.GetProperty("checkedInAtUtc").GetDateTime();
        at.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(2));

        (await (await c.Owner.Client.GetAsync($"/api/v1/appointments/{id}")).DataAsync(200)).GetProperty("checkedInAtUtc").GetDateTime().Should().Be(at);
        var list = await (await c.Owner.Client.GetAsync($"/api/v1/appointments?date={Scenarios.LocalDateIso(1)}")).DataAsync(200);
        list.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id).GetProperty("checkedInAtUtc").GetDateTime().Should().Be(at);
    }

    // ------------------------------------------------------------------ D6 patient deletion

    [Fact]
    public async Task Patient_with_appointments_or_clinical_history_cannot_be_deleted_and_stays_visible()
    {
        var c = await factory.BookableClinicAsync();

        // Fresh registration mistake: deletable.
        var mistake = (await c.Owner.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0559876543"))).GetProperty("id").GetGuid();
        (await c.Owner.Client.DeleteAsync($"/api/v1/patients/{mistake}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Only a cancelled appointment: still history.
        var cancelledOnly = (await c.Owner.Client.CreatePatientAsync(TestApi.NewPatient(mobile: "0559876544"))).GetProperty("id").GetGuid();
        var appt = await (await c.Owner.Client.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = cancelledOnly, doctorId = c.DoctorId, branchId = c.BranchId, startUtc = Scenarios.RiyadhUtc(2, 10), durationMinutes = 15, source = "Reception",
        })).DataAsync(201);
        await (await c.Owner.Client.PostAsJsonAsync($"/api/v1/appointments/{appt.GetProperty("id").GetGuid()}/cancel",
            new { rowVersion = appt.GetProperty("rowVersion").GetString(), reason = "طلب المريض" })).EnsureStatusAsync(200);
        await AssertNotDeletableAsync(c, cancelledOnly);

        // A visit (and prescription) without any appointment.
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        await c.Owner.Client.PrescriptionIdAsync(visitId);
        await AssertNotDeletableAsync(c, c.PatientId);

        // Another clinic can neither delete nor learn anything.
        var other = await factory.BookableClinicAsync();
        (await (await other.Owner.Client.DeleteAsync($"/api/v1/patients/{c.PatientId}")).ErrorCodeAsync()).Should().Be("PATIENT_NOT_FOUND");
    }

    // ------------------------------------------------------------------ the full clinical loop

    [Fact]
    public async Task Reception_to_doctor_to_prescription_to_history_with_every_role_checked()
    {
        var c = await factory.BookableClinicAsync();
        var (doctor, _, doctorId) = await TreatingDoctorAsync(c);
        var (otherDoctor, _, _) = await TreatingDoctorAsync(c);
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");

        // Reception books for the treating doctor and checks the patient in.
        var booked = await (await reception.PostAsJsonAsync("/api/v1/appointments", new
        {
            patientId = c.PatientId, doctorId, branchId = c.BranchId, startUtc = Scenarios.RiyadhUtc(1, 9), durationMinutes = 15, source = "Reception",
        })).DataAsync(201);
        var apptId = booked.GetProperty("id").GetGuid();
        var checkedIn = await (await reception.PostAsJsonAsync($"/api/v1/appointments/{apptId}/check-in",
            new { rowVersion = booked.GetProperty("rowVersion").GetString() })).DataAsync(200);
        checkedIn.TryGetProperty("checkedInAtUtc", out _).Should().BeTrue();

        // Reception cannot start the visit.
        (await reception.PostAsJsonAsync("/api/v1/visits", new { patientId = c.PatientId, doctorId, appointmentId = apptId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The doctor's queue (own appointments) shows it.
        var queue = await (await doctor.GetAsync($"/api/v1/appointments?date={Scenarios.LocalDateIso(1)}&doctorId={doctorId}")).DataAsync(200);
        queue.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).Should().Contain(apptId);

        // Start the visit with vitals, then add findings and diagnosis.
        var started = await (await doctor.PostAsJsonAsync("/api/v1/visits", new
        {
            patientId = c.PatientId, doctorId, appointmentId = apptId,
            clinical = new { vitals = new { systolicBp = 118, diastolicBp = 76, heartRate = 72, temperatureC = 37.1m, oxygenSaturation = 99, weightKg = 70m } },
        })).DataAsync(201);
        var visitId = started.GetProperty("id").GetGuid();
        var updated = await (await doctor.PutAsJsonAsync($"/api/v1/visits/{visitId}", new
        {
            clinical = ClinicalScenarios.Clinical(), followUpDate = (string?)null, followUpNotes = (string?)null,
            rowVersion = started.GetProperty("rowVersion").GetString(),
        })).DataAsync(200);
        updated.GetProperty("clinical").GetProperty("diagnosisCode").GetString().Should().Be("J02.9");

        // Another doctor (own-records scope) can neither read, edit, complete nor prescribe on it.
        (await otherDoctor.GetAsync($"/api/v1/visits/{visitId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await otherDoctor.PostAsJsonAsync($"/api/v1/visits/{visitId}/complete", new { rowVersion = updated.GetProperty("rowVersion").GetString() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await otherDoctor.IssuePrescriptionAsync(visitId)).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");

        // The owner sees everything but is not this visit's doctor: may not prescribe on it.
        (await (await c.Owner.Client.IssuePrescriptionAsync(visitId)).ErrorCodeAsync()).Should().Be("PRESCRIBER_NOT_TREATING_DOCTOR");

        // Prescription by the treating doctor while the visit is open.
        var rx = await (await doctor.IssuePrescriptionAsync(visitId)).DataAsync(201);
        rx.GetProperty("doctorId").GetGuid().Should().Be(doctorId);
        rx.GetProperty("patientId").GetGuid().Should().Be(c.PatientId);
        var rxId = rx.GetProperty("id").GetGuid();

        // Complete: the visit locks and the appointment completes.
        var completed = await (await doctor.PostAsJsonAsync($"/api/v1/visits/{visitId}/complete",
            new { rowVersion = updated.GetProperty("rowVersion").GetString() })).DataAsync(200);
        completed.GetProperty("status").GetString().Should().Be("Completed");
        (await (await c.Owner.Client.GetAsync($"/api/v1/appointments/{apptId}")).DataAsync(200)).GetProperty("status").GetString().Should().Be("Completed");
        var edit = await doctor.PutAsJsonAsync($"/api/v1/visits/{visitId}", new
        {
            clinical = ClinicalScenarios.Clinical("changed"), rowVersion = completed.GetProperty("rowVersion").GetString(),
        });
        (await edit.ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");
        // ... including new prescriptions (QA-15, decided at MVP acceptance).
        (await (await doctor.IssuePrescriptionAsync(visitId)).ErrorCodeAsync()).Should().Be("VISIT_COMPLETED");

        // History: the doctor and the owner see the visit (with diagnosis) and the prescription.
        foreach (var client in new[] { doctor, c.Owner.Client })
        {
            var visits = await (await client.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).DataAsync(200);
            visits.EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == visitId).GetProperty("diagnosis").GetString().Should().Be("التهاب البلعوم الحاد");
            var rxs = await (await client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).DataAsync(200);
            rxs.EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).Should().Contain(rxId);
        }

        // Reception: no clinical history, no prescriptions, even by direct id.
        (await reception.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.GetAsync($"/api/v1/visits/{visitId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.GetAsync($"/api/v1/prescriptions/{rxId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The other doctor's history of this patient is empty (own records only).
        (await (await otherDoctor.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).DataAsync(200)).GetArrayLength().Should().Be(0);
        (await otherDoctor.GetAsync($"/api/v1/prescriptions/{rxId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Another clinic: nothing by direct id.
        var other = await factory.BookableClinicAsync();
        (await other.Owner.Client.GetAsync($"/api/v1/visits/{visitId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Owner.Client.GetAsync($"/api/v1/prescriptions/{rxId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await other.Owner.Client.IssuePrescriptionAsync(visitId)).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
    }

    [Fact]
    public async Task A_doctor_restricted_to_another_branch_cannot_reach_this_branchs_visit_even_with_ViewAllDoctors()
    {
        var c = await factory.BookableClinicAsync();
        var second = await c.Owner.AddBranchAsync();
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        var rxId = await c.Owner.Client.PrescriptionIdAsync(visitId);

        var role = await c.Owner.Client.CreateRoleAsync("طبيب فرع",
            "Patients.View", "MedicalRecords.View", "MedicalRecords.Edit", "MedicalRecords.ViewAllDoctors", "Doctors.View",
            "Visits.View", "Visits.Create", "Visits.Edit", "Prescriptions.View", "Prescriptions.Create");
        var (branchDoctor, seat, _) = await factory.InviteWithRolesAsync(c.Owner.Client, [role], [second], prefix: "branchdoc");
        await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(second));

        (await branchDoctor.GetAsync($"/api/v1/visits/{visitId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await branchDoctor.GetAsync($"/api/v1/prescriptions/{rxId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await (await branchDoctor.GetAsync($"/api/v1/patients/{c.PatientId}/visits")).DataAsync(200)).GetArrayLength().Should().Be(0);
        (await (await branchDoctor.IssuePrescriptionAsync(visitId)).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A member with the system doctor role and their own profile working all week at the main branch.</summary>
    private async Task<(HttpClient Client, Guid Seat, Guid DoctorId)> TreatingDoctorAsync(Scenarios.BookableClinic c)
    {
        var (client, seat, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor");
        var doctor = await c.Owner.Client.CreateDoctorAsync(seat, Scenarios.Week(c.BranchId));
        return (client, seat, doctor.GetProperty("id").GetGuid());
    }

    private static async Task AssertNotDeletableAsync(Scenarios.BookableClinic c, Guid patientId)
    {
        var res = await c.Owner.Client.DeleteAsync($"/api/v1/patients/{patientId}");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.ErrorCodeAsync()).Should().Be("PATIENT_HAS_HISTORY");
        await (await c.Owner.Client.GetAsync($"/api/v1/patients/{patientId}")).EnsureStatusAsync(200);
    }
}
