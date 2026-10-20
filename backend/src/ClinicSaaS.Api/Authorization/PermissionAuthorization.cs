using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Api.Authorization;

/// <summary>Requires a fine-grained permission, e.g. <c>[HasPermission(Permissions.Patients.View)]</c>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class HasPermissionAttribute(string permission) : AuthorizeAttribute(PermissionPolicyProvider.Prefix + permission);

/// <summary>Any active member of the current clinic (a token bound to a tenant), regardless of role.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class TenantMemberAttribute() : AuthorizeAttribute(TenantMemberPolicy)
{
    public const string TenantMemberPolicy = "tenant-member";
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>Creates "perm:X" policies on demand, so there is no policy registration to forget.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public const string Prefix = "perm:";

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (!policyName.StartsWith(Prefix, StringComparison.Ordinal)) return await base.GetPolicyAsync(policyName);
        return new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName[Prefix.Length..]))
            .Build();
    }
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = context.User;
        if (!user.HasClaim(AppClaims.Permission, requirement.Permission)) return Task.CompletedTask;

        // The two planes never mix. Tenant permissions are only meaningful inside a tenant session — a platform token
        // can't use them; every other (platform) permission requires a platform session — a token bound to a clinic
        // can't use it, even if it somehow carries the claim (defence in depth for QA-03).
        var hasTenant = user.HasClaim(c => c.Type == AppClaims.TenantId);
        if (Permissions.IsTenantPermission(requirement.Permission) != hasTenant)
            return Task.CompletedTask;

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>Writes the unified error body for 401/403 and audits permission denials.</summary>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext http, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Succeeded)
        {
            await _default.HandleAsync(next, http, policy, result);
            return;
        }

        var request = http.RequestServices.GetRequiredService<IRequestContext>();
        if (result.Forbidden)
        {
            var denied = policy.Requirements.OfType<PermissionRequirement>().Select(r => r.Permission).ToArray();
            var audit = http.RequestServices.GetRequiredService<IAuditLogger>();
            audit.Add(AuditActions.AccessDenied, result: AuditResult.Denied,
                details: new { path = http.Request.Path.Value, method = http.Request.Method, permissions = denied });
            await http.RequestServices.GetRequiredService<AppDbContext>().SaveChangesAsync(http.RequestAborted);

            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new ApiError(ErrorCodes.Forbidden,
                ErrorCatalog.Message(ErrorCodes.Forbidden, request.Language), request.CorrelationId));
            return;
        }

        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await http.Response.WriteAsJsonAsync(new ApiError(ErrorCodes.Unauthorized,
            ErrorCatalog.Message(ErrorCodes.Unauthorized, request.Language), request.CorrelationId));
    }
}
