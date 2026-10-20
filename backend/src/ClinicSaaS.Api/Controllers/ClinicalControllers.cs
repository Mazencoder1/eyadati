using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Appointments;
using ClinicSaaS.Application.Doctors;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Clinical;
using Microsoft.AspNetCore.Mvc;

namespace ClinicSaaS.Api.Controllers;

[ApiController]
[Route("api/v1/doctors")]
public sealed class DoctorsController(DoctorService doctors, AppointmentService appointments) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Doctors.View)]
    public async Task<ActionResult<ApiPagedResponse<DoctorDto>>> List([FromQuery] DoctorListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<DoctorDto>.From(await doctors.ListAsync(query, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Doctors.View)]
    public async Task<ActionResult<ApiResponse<DoctorDto>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<DoctorDto>(await doctors.GetAsync(id, ct)));

    [HttpPost]
    [HasPermission(Permissions.Doctors.Manage)]
    public async Task<ActionResult<ApiResponse<DoctorDto>>> Create(CreateDoctorRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, new ApiResponse<DoctorDto>(await doctors.CreateAsync(request, ct)));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Doctors.Manage)]
    public async Task<ActionResult<ApiResponse<DoctorDto>>> Update(Guid id, UpdateDoctorRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<DoctorDto>(await doctors.UpdateAsync(id, request, ct)));

    [HttpPut("{id:guid}/schedule")]
    [HasPermission(Permissions.Doctors.Manage)]
    public async Task<ActionResult<ApiResponse<DoctorDto>>> UpdateSchedule(Guid id, UpdateScheduleRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<DoctorDto>(await doctors.UpdateScheduleAsync(id, request, ct)));

    /// <summary>Free slots on a clinic-local date, e.g. <c>?date=2026-10-08&amp;serviceId=…</c>.</summary>
    [HttpGet("{id:guid}/availability")]
    [HasPermission(Permissions.Appointments.View)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<AvailableSlot>>>> Availability(
        Guid id, [FromQuery] DateOnly date, [FromQuery] Guid? branchId, [FromQuery] Guid? serviceId, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<AvailableSlot>>(await appointments.GetAvailabilityAsync(id, date, branchId, serviceId, ct)));
}

[ApiController]
[Route("api/v1/services")]
public sealed class ServicesController(ClinicServiceCatalog services) : ControllerBase
{
    [HttpGet]
    [TenantMember]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ClinicServiceDto>>>> List([FromQuery] bool activeOnly, CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<ClinicServiceDto>>(await services.ListAsync(activeOnly, ct)));

    [HttpPost]
    [HasPermission(Permissions.Settings.Manage)]
    public async Task<ActionResult<ApiResponse<ClinicServiceDto>>> Create(SaveClinicServiceRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, new ApiResponse<ClinicServiceDto>(await services.CreateAsync(request, ct)));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Settings.Manage)]
    public async Task<ActionResult<ApiResponse<ClinicServiceDto>>> Update(Guid id, SaveClinicServiceRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<ClinicServiceDto>(await services.UpdateAsync(id, request, ct)));

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Settings.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await services.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/v1/appointments")]
public sealed class AppointmentsController(AppointmentService appointments) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Appointments.View)]
    public async Task<ActionResult<ApiPagedResponse<AppointmentDto>>> List([FromQuery] AppointmentListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<AppointmentDto>.From(await appointments.ListAsync(query, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Appointments.View)]
    public async Task<ActionResult<ApiResponse<AppointmentDto>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<AppointmentDto>(await appointments.GetAsync(id, ct)));

    [HttpPost]
    [HasPermission(Permissions.Appointments.Create)]
    public async Task<ActionResult<ApiResponse<AppointmentDto>>> Create(CreateAppointmentRequest request, CancellationToken ct)
    {
        var created = await appointments.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ApiResponse<AppointmentDto>(created));
    }

    [HttpPut("{id:guid}/reschedule")]
    [HasPermission(Permissions.Appointments.Edit)]
    public async Task<ActionResult<ApiResponse<AppointmentDto>>> Reschedule(Guid id, RescheduleAppointmentRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<AppointmentDto>(await appointments.RescheduleAsync(id, request, ct)));

    [HttpPost("{id:guid}/confirm")]
    [HasPermission(Permissions.Appointments.Edit)]
    public Task<ActionResult<ApiResponse<AppointmentDto>>> Confirm(Guid id, ChangeAppointmentStatusRequest request, CancellationToken ct) =>
        Change(id, AppointmentStatus.Confirmed, request, ct);

    [HttpPost("{id:guid}/check-in")]
    [HasPermission(Permissions.Appointments.CheckIn)]
    public Task<ActionResult<ApiResponse<AppointmentDto>>> CheckIn(Guid id, ChangeAppointmentStatusRequest request, CancellationToken ct) =>
        Change(id, AppointmentStatus.CheckedIn, request, ct);

    [HttpPost("{id:guid}/complete")]
    [HasPermission(Permissions.Appointments.CheckIn)]
    public Task<ActionResult<ApiResponse<AppointmentDto>>> Complete(Guid id, ChangeAppointmentStatusRequest request, CancellationToken ct) =>
        Change(id, AppointmentStatus.Completed, request, ct);

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Appointments.Cancel)]
    public Task<ActionResult<ApiResponse<AppointmentDto>>> Cancel(Guid id, ChangeAppointmentStatusRequest request, CancellationToken ct) =>
        Change(id, AppointmentStatus.Cancelled, request, ct);

    [HttpPost("{id:guid}/no-show")]
    [HasPermission(Permissions.Appointments.Edit)]
    public Task<ActionResult<ApiResponse<AppointmentDto>>> NoShow(Guid id, ChangeAppointmentStatusRequest request, CancellationToken ct) =>
        Change(id, AppointmentStatus.NoShow, request, ct);

    private async Task<ActionResult<ApiResponse<AppointmentDto>>> Change(Guid id, AppointmentStatus target, ChangeAppointmentStatusRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<AppointmentDto>(await appointments.ChangeStatusAsync(id, target, request, ct)));
}
