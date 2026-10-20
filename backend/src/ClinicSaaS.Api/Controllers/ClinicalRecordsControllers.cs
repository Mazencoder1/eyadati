using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Prescriptions;
using ClinicSaaS.Application.Visits;
using ClinicSaaS.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicSaaS.Api.Controllers;

/// <summary>
/// Visits. Visits.* gate the endpoints; the clinical block of a visit additionally needs MedicalRecords.View to be
/// returned and MedicalRecords.Edit to be written (enforced in <see cref="VisitService"/>).
/// </summary>
[ApiController]
[Route("api/v1/visits")]
public sealed class VisitsController(VisitService visits) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Visits.View)]
    public async Task<ActionResult<ApiPagedResponse<VisitListItem>>> List([FromQuery] VisitListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<VisitListItem>.From(await visits.ListAsync(query, ct)));

    [HttpGet("/api/v1/patients/{patientId:guid}/visits")]
    [HasPermission(Permissions.Visits.View)]
    public async Task<ActionResult<ApiPagedResponse<VisitListItem>>> ListForPatient(Guid patientId, [FromQuery] VisitListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<VisitListItem>.From(await visits.ListForPatientAsync(patientId, query, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Visits.View)]
    public async Task<ActionResult<ApiResponse<VisitDto>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<VisitDto>(await visits.GetAsync(id, ct)));

    [HttpPost]
    [HasPermission(Permissions.Visits.Create)]
    public async Task<ActionResult<ApiResponse<VisitDto>>> Create(CreateVisitRequest request, CancellationToken ct)
    {
        var created = await visits.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ApiResponse<VisitDto>(created));
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Visits.Edit)]
    public async Task<ActionResult<ApiResponse<VisitDto>>> Update(Guid id, UpdateVisitRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<VisitDto>(await visits.UpdateAsync(id, request, ct)));

    /// <summary>
    /// Completes the visit (locks it) and its checked-in appointment (T-013b). Needs Visits.Edit and, because it
    /// finalizes the clinical content, MedicalRecords.Edit.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [HasPermission(Permissions.Visits.Edit)]
    [HasPermission(Permissions.MedicalRecords.Edit)]
    public async Task<ActionResult<ApiResponse<VisitDto>>> Complete(Guid id, CompleteVisitRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<VisitDto>(await visits.CompleteAsync(id, request, ct)));
}

/// <summary>
/// Prescriptions: issued once, never edited or deleted through the API (a correction is a new prescription).
/// Reading one (content = medications) needs MedicalRecords.View as well as Prescriptions.View (T-012).
/// </summary>
[ApiController]
[Route("api/v1/prescriptions")]
public sealed class PrescriptionsController(PrescriptionService prescriptions) : ControllerBase
{
    [HttpGet("/api/v1/patients/{patientId:guid}/prescriptions")]
    [HasPermission(Permissions.Prescriptions.View)]
    [HasPermission(Permissions.MedicalRecords.View)]
    public async Task<ActionResult<ApiPagedResponse<PrescriptionListItem>>> ListForPatient(
        Guid patientId, [FromQuery] PrescriptionListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<PrescriptionListItem>.From(await prescriptions.ListForPatientAsync(patientId, query, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Prescriptions.View)]
    [HasPermission(Permissions.MedicalRecords.View)]
    public async Task<ActionResult<ApiResponse<PrescriptionDto>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<PrescriptionDto>(await prescriptions.GetAsync(id, ct)));

    [HttpPost]
    [HasPermission(Permissions.Prescriptions.Create)]
    public async Task<ActionResult<ApiResponse<PrescriptionDto>>> Create(CreatePrescriptionRequest request, CancellationToken ct)
    {
        var created = await prescriptions.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ApiResponse<PrescriptionDto>(created));
    }
}

/// <summary>
/// Follow-ups due, for booking them: operational fields only (patient, date, doctor, booked or not). Gated by
/// Appointments.View like the appointment board — no clinical content, so no Visits.View/MedicalRecords.View needed.
/// </summary>
[ApiController]
[Route("api/v1/follow-ups")]
public sealed class FollowUpsController(FollowUpQueries followUps) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Appointments.View)]
    public async Task<ActionResult<ApiPagedResponse<FollowUpItem>>> List([FromQuery] FollowUpQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<FollowUpItem>.From(await followUps.ListAsync(query, ct)));
}
