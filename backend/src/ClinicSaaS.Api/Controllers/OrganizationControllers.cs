using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Organization;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Application.Staff;
using ClinicSaaS.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClinicSaaS.Api.Controllers;

[ApiController]
[Route("api/v1/settings")]
public sealed class SettingsController(ClinicSettingsService settings) : ControllerBase
{
    [HttpGet]
    [TenantMember]
    public async Task<ActionResult<ApiResponse<ClinicSettingsDto>>> Get(CancellationToken ct) =>
        Ok(new ApiResponse<ClinicSettingsDto>(await settings.GetAsync(ct)));

    [HttpPut]
    [HasPermission(Permissions.Settings.Manage)]
    public async Task<ActionResult<ApiResponse<ClinicSettingsDto>>> Update(UpdateClinicSettingsRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<ClinicSettingsDto>(await settings.UpdateAsync(request, ct)));
}

[ApiController]
[Route("api/v1/branches")]
public sealed class BranchesController(BranchService branches) : ControllerBase
{
    [HttpGet]
    [TenantMember]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<BranchDto>>>> List(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<BranchDto>>(await branches.ListAsync(ct)));

    [HttpPost]
    [HasPermission(Permissions.Branches.Manage)]
    public async Task<ActionResult<ApiResponse<BranchDto>>> Create(SaveBranchRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, new ApiResponse<BranchDto>(await branches.CreateAsync(request, ct)));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Branches.Manage)]
    public async Task<ActionResult<ApiResponse<BranchDto>>> Update(Guid id, SaveBranchRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<BranchDto>(await branches.UpdateAsync(id, request, ct)));
}

[ApiController]
[Route("api/v1")]
public sealed class RolesController(RoleService roles) : ControllerBase
{
    [HttpGet("permissions")]
    [HasPermission(Permissions.Roles.Manage)]
    public ActionResult<ApiResponse<IReadOnlyList<PermissionGroup>>> Catalog() =>
        Ok(new ApiResponse<IReadOnlyList<PermissionGroup>>(RoleService.Catalog()));

    [HttpGet("roles")]
    [HasPermission(Permissions.Staff.View)]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<RoleDto>>>> List(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<RoleDto>>(await roles.ListAsync(ct)));

    [HttpPost("roles")]
    [HasPermission(Permissions.Roles.Manage)]
    public async Task<ActionResult<ApiResponse<RoleDto>>> Create(SaveRoleRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, new ApiResponse<RoleDto>(await roles.CreateAsync(request, ct)));

    [HttpPut("roles/{id:guid}")]
    [HasPermission(Permissions.Roles.Manage)]
    public async Task<ActionResult<ApiResponse<RoleDto>>> Update(Guid id, SaveRoleRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<RoleDto>(await roles.UpdateAsync(id, request, ct)));

    [HttpDelete("roles/{id:guid}")]
    [HasPermission(Permissions.Roles.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await roles.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/v1/staff")]
public sealed class StaffController(StaffService staff) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Staff.View)]
    public async Task<ActionResult<ApiPagedResponse<StaffMemberDto>>> List([FromQuery] StaffListQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<StaffMemberDto>.From(await staff.ListAsync(query, ct)));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Staff.View)]
    public async Task<ActionResult<ApiResponse<StaffMemberDto>>> Get(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<StaffMemberDto>(await staff.GetAsync(id, ct)));

    [HttpPost]
    [HasPermission(Permissions.Staff.Manage)]
    public async Task<ActionResult<ApiResponse<StaffMemberDto>>> Invite(InviteStaffRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, new ApiResponse<StaffMemberDto>(await staff.InviteAsync(request, ct)));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Staff.Manage)]
    public async Task<ActionResult<ApiResponse<StaffMemberDto>>> Update(Guid id, UpdateStaffRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<StaffMemberDto>(await staff.UpdateAsync(id, request, ct)));

    [HttpPost("{id:guid}/disable")]
    [HasPermission(Permissions.Staff.Manage)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        await staff.SetStatusAsync(id, enabled: false, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/enable")]
    [HasPermission(Permissions.Staff.Manage)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct)
    {
        await staff.SetStatusAsync(id, enabled: true, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/resend-invitation")]
    [HasPermission(Permissions.Staff.Manage)]
    public async Task<IActionResult> ResendInvitation(Guid id, CancellationToken ct)
    {
        await staff.ResendInvitationAsync(id, ct);
        return Accepted();
    }
}

[ApiController]
[Route("api/v1")]
public sealed class SubscriptionController(SubscriptionQueries subscriptions, AuditLogQueries auditLogs) : ControllerBase
{
    [HttpGet("subscription")]
    [HasPermission(Permissions.Subscription.View)]
    public async Task<ActionResult<ApiResponse<SubscriptionView?>>> Current(CancellationToken ct) =>
        Ok(new ApiResponse<SubscriptionView?>(await subscriptions.GetCurrentAsync(ct)));

    [HttpGet("audit-logs")]
    [HasPermission(Permissions.AuditLogs.View)]
    public async Task<ActionResult<ApiPagedResponse<AuditLogItem>>> AuditLogs([FromQuery] AuditLogQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<AuditLogItem>.From(await auditLogs.ListAsync(query, ct)));
}
