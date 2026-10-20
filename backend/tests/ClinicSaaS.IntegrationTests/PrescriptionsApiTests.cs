using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// T-006 + D8: prescriptions API — numbering, items, derivation from the visit, prescriber rule, permissions, tenant
/// and branch isolation, immutability. In <see cref="Scenarios.BookableClinic"/> the owner is also the clinic's doctor.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PrescriptionsApiTests(ApiFactory factory)
{
    [Fact]
    public async Task Prescriptions_are_numbered_per_clinic_with_ordered_items()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = (await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c))).GetProperty("id").GetGuid();

        var first = await c.Owner.Client.IssuePrescriptionAsync(visitId);
        await first.EnsureStatusAsync(201);
        var rx = (await first.ReadJsonAsync()).GetProperty("data");
        rx.GetProperty("number").GetString().Should().Be("RX-000001");
        rx.GetProperty("visitId").GetGuid().Should().Be(visitId);
        rx.GetProperty("patientMrn").GetString().Should().Be("P-000001");
        rx.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("lineNumber").GetInt32()).Should().Equal(1, 2);
        rx.TryGetProperty("rowVersion", out _).Should().BeFalse(); // immutable document

        var second = await c.Owner.Client.IssuePrescriptionAsync(visitId, ClinicalScenarios.RxItems(1));
        (await second.ReadJsonAsync()).GetProperty("data").GetProperty("number").GetString().Should().Be("RX-000002");

        var get = await c.Owner.Client.GetAsync($"/api/v1/prescriptions/{rx.GetProperty("id").GetGuid()}");
        await get.EnsureStatusAsync(200);
        (await get.ReadJsonAsync()).GetProperty("data").GetProperty("items")[1].GetProperty("medicationName").GetString().Should().Be("Paracetamol 2");

        var list = await (await c.Owner.Client.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).ReadJsonAsync();
        list.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(2);
        list.GetProperty("data")[0].GetProperty("number").GetString().Should().Be("RX-000002"); // newest first
        list.GetProperty("data")[1].GetProperty("itemCount").GetInt32().Should().Be(2);

        // Each clinic has its own sequence.
        var other = await factory.BookableClinicAsync();
        var otherRx = await other.Owner.Client.IssuePrescriptionAsync(await other.Owner.Client.VisitIdAsync(other));
        (await otherRx.ReadJsonAsync()).GetProperty("data").GetProperty("number").GetString().Should().Be("RX-000001");
    }

    [Fact]
    public async Task Prescriptions_cannot_be_edited_or_deleted()
    {
        var c = await factory.BookableClinicAsync();
        var id = await c.Owner.Client.PrescriptionIdAsync(await c.Owner.Client.VisitIdAsync(c));

        (await c.Owner.Client.PutAsJsonAsync($"/api/v1/prescriptions/{id}", new { notes = "x" })).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        (await c.Owner.Client.DeleteAsync($"/api/v1/prescriptions/{id}")).StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Items_and_visit_are_validated()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        (await c.Owner.Client.IssuePrescriptionAsync(visitId, [])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await c.Owner.Client.IssuePrescriptionAsync(visitId, ClinicalScenarios.RxItems(31))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var badItem = await c.Owner.Client.IssuePrescriptionAsync(visitId, [new { medicationName = " ", durationDays = 400 }]);
        badItem.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await badItem.ErrorCodeAsync()).Should().Be("VALIDATION_FAILED");

        // D8: a visit is required.
        var noVisit = await c.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new { items = ClinicalScenarios.RxItems() });
        noVisit.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await noVisit.ReadJsonAsync()).GetProperty("errors").TryGetProperty("visitId", out _).Should().BeTrue();
        (await (await c.Owner.Client.IssuePrescriptionAsync(Guid.NewGuid())).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
    }

    [Fact]
    public async Task Patient_and_doctor_come_from_the_visit_and_client_supplied_ids_are_ignored()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        var otherPatient = (await c.Owner.Client.CreatePatientAsync()).GetProperty("id").GetGuid();

        // Old-style body naming another patient and a random doctor: those fields are not part of the contract.
        var res = await c.Owner.Client.PostAsJsonAsync("/api/v1/prescriptions", new
        {
            visitId, patientId = otherPatient, doctorId = Guid.NewGuid(), branchId = Guid.NewGuid(), items = ClinicalScenarios.RxItems(1),
        });
        await res.EnsureStatusAsync(201);
        var rx = (await res.ReadJsonAsync()).GetProperty("data");
        rx.GetProperty("patientId").GetGuid().Should().Be(c.PatientId);
        rx.GetProperty("doctorId").GetGuid().Should().Be(c.DoctorId);
    }

    [Fact]
    public async Task Receptionist_cannot_read_or_issue_and_nurse_can_only_read()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        var id = await c.Owner.Client.PrescriptionIdAsync(visitId);
        var (reception, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "receptionist");
        var (nurse, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "nurse");

        (await reception.GetAsync($"/api/v1/prescriptions/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await reception.IssuePrescriptionAsync(visitId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await (await nurse.GetAsync($"/api/v1/prescriptions/{id}")).EnsureStatusAsync(200);
        (await nurse.IssuePrescriptionAsync(visitId)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Audit_log_records_issue_and_view_without_medications()
    {
        var c = await factory.BookableClinicAsync();
        var visitId = await c.Owner.Client.VisitIdAsync(c);
        var id = (await c.Owner.Client.PrescriptionIdAsync(visitId)).ToString();
        await (await c.Owner.Client.GetAsync($"/api/v1/prescriptions/{id}")).EnsureStatusAsync(200);

        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            var rows = await db.AuditLogs.Where(a => a.EntityId == id).ToListAsync();
            rows.Select(r => r.Action).Should().Contain(["prescription.issued", "prescription.viewed"]);
            rows.Should().AllSatisfy(r => (r.Details ?? "").Should().NotContain("Paracetamol"));
            rows.Single(r => r.Action == "prescription.issued").Details.Should().Contain(visitId.ToString());
        });
    }

    [Fact]
    public async Task Tenant_B_cannot_read_list_or_reference_tenant_A_prescriptions()
    {
        var a = await factory.BookableClinicAsync();
        var visitOfA = (await a.Owner.Client.RecordVisitAsync(a, await a.Owner.Client.CheckedInAppointmentAsync(a))).GetProperty("id").GetGuid();
        var rxOfA = await a.Owner.Client.PrescriptionIdAsync(visitOfA);
        var b = await factory.BookableClinicAsync();
        var client = b.Owner.Client;

        var get = await client.GetAsync($"/api/v1/prescriptions/{rxOfA}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await get.ErrorCodeAsync()).Should().Be("PRESCRIPTION_NOT_FOUND");
        (await client.GetAsync($"/api/v1/patients/{a.PatientId}/prescriptions")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // B (a doctor in its own clinic) cannot issue on A's visit.
        (await (await client.IssuePrescriptionAsync(visitOfA)).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");

        // B's own list stays empty and A's sequence was not consumed by B's failed attempt.
        var bList = await (await client.GetAsync($"/api/v1/patients/{b.PatientId}/prescriptions")).ReadJsonAsync();
        bList.GetProperty("meta").GetProperty("totalCount").GetInt32().Should().Be(0);
        var next = await a.Owner.Client.IssuePrescriptionAsync(visitOfA);
        (await next.ReadJsonAsync()).GetProperty("data").GetProperty("number").GetString().Should().Be("RX-000002");
    }

    [Fact]
    public async Task Branch_restricted_members_do_not_see_prescriptions_of_other_branches_visits()
    {
        var c = await factory.BookableClinicAsync();
        var secondBranch = await c.Owner.AddBranchAsync();
        var visitInMain = (await c.Owner.Client.RecordVisitAsync(c, await c.Owner.Client.CheckedInAppointmentAsync(c))).GetProperty("id").GetGuid();
        var rxInMain = await c.Owner.Client.PrescriptionIdAsync(visitInMain);

        // A prescription from before D8 (no visit) can no longer be created through the API; seed one directly.
        var legacy = Guid.NewGuid();
        await factory.WithDbAsync(c.Owner.TenantId, async db =>
        {
            db.Prescriptions.Add(new Prescription
            {
                Id = legacy, TenantId = c.Owner.TenantId, Number = "RX-LEGACY", PatientId = c.PatientId, DoctorId = c.DoctorId,
                IssuedAtUtc = DateTime.UtcNow,
                Items = [new PrescriptionItem { TenantId = c.Owner.TenantId, LineNumber = 1, MedicationName = "Amoxicillin" }],
            });
            await db.SaveChangesAsync();
        });

        var (doctor, _, _) = await factory.InviteAndAcceptAsync(c.Owner, "doctor", [secondBranch]);

        (await doctor.GetAsync($"/api/v1/prescriptions/{rxInMain}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await (await doctor.GetAsync($"/api/v1/prescriptions/{legacy}")).EnsureStatusAsync(200);
        var list = await (await doctor.GetAsync($"/api/v1/patients/{c.PatientId}/prescriptions")).ReadJsonAsync();
        list.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).Should().Equal(legacy);

        // …and cannot attach a new prescription to the other branch's visit.
        (await (await doctor.IssuePrescriptionAsync(visitInMain)).ErrorCodeAsync()).Should().Be("VISIT_NOT_FOUND");
    }
}
