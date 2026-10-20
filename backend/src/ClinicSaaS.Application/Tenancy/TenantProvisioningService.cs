using System.Security.Cryptography;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Application.Tenancy;

public sealed class SaasOptions
{
    public const string Section = "Saas";
    /// <summary>Plan code a self-registered clinic starts on (Phase 2: a trial of "basic", D17 Trial → Paid).</summary>
    public string DefaultPlanCode { get; set; } = "basic";

    /// <summary>
    /// Plan codes left out of the public catalog (GET /plans) without touching plan data or migrations. Clinics
    /// already on such a plan keep it and still see it on their subscription page.
    /// </summary>
    public string[] CatalogHiddenPlanCodes { get; set; } = [];
}

public sealed record NewTenantSpec(string ClinicName, string? Slug, string City, string ClinicPhone, string? Email);

public sealed record ProvisionedTenant(Tenant Tenant, TenantMembership OwnerMembership);

/// <summary>
/// Creates a clinic with everything it needs to work: main branch, role set from templates, owner membership,
/// subscription on the default plan and its number sequences. Must be called inside a transaction.
/// </summary>
public sealed class TenantProvisioningService(
    IAppDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IAuditLogger audit,
    IOptions<SaasOptions> saas)
{
    public async Task<ProvisionedTenant> ProvisionAsync(NewTenantSpec spec, User owner, CancellationToken ct)
    {
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == saas.Value.DefaultPlanCode && p.IsActive, ct)
                   ?? throw new InvalidOperationException($"Default plan '{saas.Value.DefaultPlanCode}' is missing.");

        var tenant = new Tenant
        {
            Name = spec.ClinicName.Trim(),
            Slug = await ResolveSlugAsync(spec.Slug, ct),
            City = spec.City.Trim(),
            Phone = SaudiFormats.NormalizeMobile(spec.ClinicPhone),
            Email = spec.Email,
            Status = TenantStatus.Active,
        };
        db.Tenants.Add(tenant);

        using var _ = tenantContext.UseTenant(tenant.Id);

        db.Branches.Add(new Branch { TenantId = tenant.Id, Name = "الفرع الرئيسي", City = tenant.City, Phone = tenant.Phone, IsMain = true });

        Role? ownerRole = null;
        foreach (var template in SystemRoles.Templates)
        {
            var role = new Role
            {
                TenantId = tenant.Id,
                Name = template.NameAr,
                NormalizedName = template.Key.ToUpperInvariant(),
                Description = template.NameEn,
                SystemKey = template.Key,
            };
            foreach (var permission in template.Permissions)
                role.Permissions.Add(new RolePermission { TenantId = tenant.Id, Permission = permission });
            db.Roles.Add(role);
            if (template.Key == SystemRoles.TenantOwner) ownerRole = role;
        }

        var membership = new TenantMembership
        {
            TenantId = tenant.Id,
            UserId = owner.Id,
            IsOwner = true,
            Status = MembershipStatus.Active,
        };
        membership.Roles.Add(new MembershipRole { TenantId = tenant.Id, RoleId = ownerRole!.Id, Role = ownerRole });
        db.TenantMemberships.Add(membership);

        db.Subscriptions.Add(Subscription.StartTrial(tenant.Id, plan, clock.UtcNow));

        foreach (var name in SequenceNames.All)
            db.NumberSequences.Add(new NumberSequence { TenantId = tenant.Id, Name = name });

        audit.Add(AuditActions.TenantRegistered, nameof(Tenant), tenant.Id.ToString(), tenantId: tenant.Id, userId: owner.Id,
            details: new { plan = plan.Code });

        await db.SaveChangesAsync(ct);
        return new ProvisionedTenant(tenant, membership);
    }

    private async Task<string> ResolveSlugAsync(string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var slug = requested.Trim().ToLowerInvariant();
            if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
                throw new RequestValidationException(new Dictionary<string, string[]> { ["slug"] = ["slug_taken"] });
            return slug;
        }

        // Clinic names are usually Arabic, so a readable transliteration isn't reliable — generate a short
        // random handle; the owner can pick a custom slug later from settings.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = "clinic-" + RandomSuffix(6);
            if (!await db.Tenants.AnyAsync(t => t.Slug == candidate, ct)) return candidate;
        }
        return "clinic-" + RandomSuffix(12);
    }

    private static string RandomSuffix(int length)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(length, alphabet, (span, a) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = a[RandomNumberGenerator.GetInt32(a.Length)];
        });
    }
}
