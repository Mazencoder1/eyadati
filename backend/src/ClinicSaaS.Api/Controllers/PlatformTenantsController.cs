using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Platform;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Platform;
using Microsoft.AspNetCore.Mvc;

namespace ClinicSaaS.Api.Controllers;

/// <summary>Super-admin clinic management. Platform permissions only — tenant tokens can't reach it.</summary>
[ApiController]
[Route("api/v1/platform/tenants")]
public sealed class PlatformTenantsController(PlatformTenantService tenants, PlatformSubscriptionService subscriptions) : ControllerBase
{
    [HttpGet]
    [HasPermission(Permissions.Platform.TenantsView)]
    public async Task<ActionResult<ApiPagedResponse<PlatformTenantItem>>> List([FromQuery] PlatformTenantQuery query, CancellationToken ct) =>
        Ok(ApiPagedResponse<PlatformTenantItem>.From(await tenants.ListAsync(query, ct)));

    [HttpPost("{id:guid}/suspend")]
    [HasPermission(Permissions.Platform.TenantsManage)]
    public async Task<IActionResult> Suspend(Guid id, CancellationToken ct)
    {
        await tenants.SetStatusAsync(id, TenantStatus.Suspended, ct);
        return NoContent();
    }

    /// <summary>The clinic's current subscription (null when it has none).</summary>
    [HttpGet("{id:guid}/subscription")]
    [HasPermission(Permissions.Platform.BillingView)]
    public async Task<ActionResult<ApiResponse<PlatformSubscriptionDto?>>> GetSubscription(Guid id, CancellationToken ct) =>
        Ok(new ApiResponse<PlatformSubscriptionDto?>(await subscriptions.GetAsync(id, ct)));

    /// <summary>Manual billing (D1): change the clinic's plan, status and billing period.</summary>
    [HttpPut("{id:guid}/subscription")]
    [HasPermission(Permissions.Platform.BillingManage)]
    public async Task<ActionResult<ApiResponse<PlatformSubscriptionDto>>> UpdateSubscription(
        Guid id, UpdatePlatformSubscriptionRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<PlatformSubscriptionDto>(await subscriptions.UpdateAsync(id, request, ct)));

    [HttpPost("{id:guid}/reactivate")]
    [HasPermission(Permissions.Platform.TenantsManage)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        await tenants.SetStatusAsync(id, TenantStatus.Active, ct);
        return NoContent();
    }
}
