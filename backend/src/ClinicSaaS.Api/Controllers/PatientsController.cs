using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Patients;
using ClinicSaaS.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicSaaS.Api.Controllers;

[ApiController]
[Route("api/v1/patients")]
public sealed class PatientsController(PatientService patients) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Patients.View)]
    public async Task<ActionResult<ApiPagedResponse<PatientListItem>>> List([FromQuery] PatientListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<PatientListItem>.From(await patients.ListAsync(query, ct)));

    [HttpGet("duplicates")]
    [HasPermission(Permissions.Patients.Create)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<DuplicateCandidate>>>> Duplicates(
        [FromQuery] string? mobile, [FromQuery] string? identityNumber, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<DuplicateCandidate>>(await patients.FindDuplicatesAsync(mobile, identityNumber, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Patients.View)]
    public async Task<ActionResult<ApiResponse<PatientDetails>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<PatientDetails>(await patients.GetAsync(id, ct)));

    [HttpPost]
    [HasPermission(Permissions.Patients.Create)]
    public async Task<ActionResult<ApiResponse<PatientDetails>>> Create(SavePatientRequest request, CancellationToken ct)
    {
        var created = await patients.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ApiResponse<PatientDetails>(created));
    }

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Patients.Edit)]
    public async Task<ActionResult<ApiResponse<PatientDetails>>> Update(Guid id, UpdatePatientRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<PatientDetails>(await patients.UpdateAsync(id, request, ct)));

    [HttpPut("{id:guid}/medical")]
    [HasPermission(Permissions.MedicalRecords.Edit)]
    public async Task<ActionResult<ApiResponse<PatientDetails>>> UpdateMedical(Guid id, UpdatePatientMedicalRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<PatientDetails>(await patients.UpdateMedicalAsync(id, request, ct)));

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Patients.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await patients.DeleteAsync(id, ct);
        return NoContent();
    }
}
