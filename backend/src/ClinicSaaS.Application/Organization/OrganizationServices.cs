using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Organization;

// ============================================================ clinic settings

public sealed record ClinicSettingsDto(
    Guid Id, string Name, string Slug, string Status, string? LegalName, string? VatNumber, string? CommercialRegistration,
    string? Email, string? Phone, string? City, string TimeZone, string Currency, string Locale, int DefaultAppointmentMinutes,
    string RowVersion);

public sealed record UpdateClinicSettingsRequest(
    string Name, string? LegalName, string? VatNumber, string? CommercialRegistration, string? Email, string? Phone,
    string? City, string TimeZone, string Locale, int DefaultAppointmentMinutes, string RowVersion);

public sealed class UpdateClinicSettingsValidator : AbstractValidator<UpdateClinicSettingsRequest>
{
    public UpdateClinicSettingsValidator()
    {
        RuleFor(x => x.Name).RequiredText().MaxLen(200);
        RuleFor(x => x.LegalName).MaxLen(200);
        RuleFor(x => x.VatNumber).Must(SaudiFormats.IsValidVatNumber).WithMessage(ValidationCodes.InvalidVatNumber)
            .When(x => !string.IsNullOrWhiteSpace(x.VatNumber));
        RuleFor(x => x.CommercialRegistration).Matches(@"^\d{10}$").WithMessage(ValidationCodes.InvalidValue)
            .When(x => !string.IsNullOrWhiteSpace(x.CommercialRegistration));
        RuleFor(x => x.Email).ValidEmail().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).SaudiMobile();
        RuleFor(x => x.City).MaxLen(100);
        RuleFor(x => x.TimeZone).Must(tz => TimeZoneInfo.TryFindSystemTimeZoneById(tz ?? "", out _)).WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.Locale).Must(l => l is "ar" or "en").WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.DefaultAppointmentMinutes).InclusiveBetween(5, 240).WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

public sealed class ClinicSettingsService(
    IAppDbContext db, ClinicContext clinic, IAuditLogger audit, IValidator<UpdateClinicSettingsRequest> validator)
{
    public async Task<ClinicSettingsDto> GetAsync(CancellationToken ct) => ToDto(await clinic.GetTenantAsync(ct));

    public async Task<ClinicSettingsDto> UpdateAsync(UpdateClinicSettingsRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        var t = await clinic.GetTenantAsync(ct);
        db.SetOriginalRowVersion(t, RowVersions.Decode(req.RowVersion));

        t.Name = req.Name.Trim();
        t.LegalName = Clean(req.LegalName);
        t.VatNumber = Clean(req.VatNumber) is { } vat ? SaudiFormats.ToLatinDigits(vat) : null;
        t.CommercialRegistration = Clean(req.CommercialRegistration) is { } cr ? SaudiFormats.ToLatinDigits(cr) : null;
        t.Email = Clean(req.Email)?.ToLowerInvariant();
        t.Phone = SaudiFormats.NormalizeMobile(req.Phone);
        t.City = Clean(req.City);
        t.TimeZone = req.TimeZone;
        t.Locale = req.Locale;
        t.DefaultAppointmentMinutes = req.DefaultAppointmentMinutes;

        audit.Add(AuditActions.SettingsUpdated, nameof(Tenant), t.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(t);
    }

    private static ClinicSettingsDto ToDto(Tenant t) => new(t.Id, t.Name, t.Slug, t.Status.ToString(), t.LegalName, t.VatNumber,
        t.CommercialRegistration, t.Email, t.Phone, t.City, t.TimeZone, t.Currency, t.Locale, t.DefaultAppointmentMinutes,
        RowVersions.Encode(t.RowVersion));

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

// ============================================================ branches

public sealed record BranchDto(
    Guid Id, string Name, string? City, string? Address, string? Phone, decimal? Latitude, decimal? Longitude,
    bool IsMain, bool IsActive, string RowVersion);

public sealed record SaveBranchRequest(
    string Name, string? City, string? Address, string? Phone, decimal? Latitude, decimal? Longitude,
    bool IsActive = true, string? RowVersion = null);

public sealed class SaveBranchValidator : AbstractValidator<SaveBranchRequest>
{
    public SaveBranchValidator()
    {
        RuleFor(x => x.Name).RequiredText().MaxLen(200);
        RuleFor(x => x.City).MaxLen(100);
        RuleFor(x => x.Address).MaxLen(500);
        RuleFor(x => x.Phone).SaudiMobile();
        RuleFor(x => x.Latitude).InclusiveBetween(-90, 90).When(x => x.Latitude is not null).WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.Longitude).InclusiveBetween(-180, 180).When(x => x.Longitude is not null).WithMessage(ValidationCodes.OutOfRange);
    }
}

/// <summary>
/// Branches. Every member may list them (names are needed for display). A branch-restricted member holding
/// Branches.Manage may edit only their own branches and may not create new ones (a new branch would lie outside their
/// set) — 403 BRANCH_ACCESS_DENIED (F2, T-011).
/// </summary>
public sealed class BranchService(
    IAppDbContext db, ClinicContext clinic, IUsageLimiter limits, IFeatureGate features, IAuditLogger audit, IValidator<SaveBranchRequest> validator)
{
    public async Task<IReadOnlyList<BranchDto>> ListAsync(CancellationToken ct) =>
        (await db.Branches.AsNoTracking().OrderByDescending(b => b.IsMain).ThenBy(b => b.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<BranchDto> CreateAsync(SaveBranchRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        if (clinic.RestrictedBranchIds.Count > 0) throw new ForbiddenException(ErrorCodes.BranchAccessDenied);
        await limits.EnsureCanAddAsync(LimitKeys.MaxBranches, ct);
        // The first branch comes with every plan; a second one is a plan feature.
        if (await db.Branches.AnyAsync(ct)) await features.EnsureEnabledAsync(FeatureKeys.MultipleBranches, ct);

        var branch = new Branch { Name = "" };
        Apply(branch, req);
        branch.IsActive = true;
        db.Branches.Add(branch);
        audit.Add(AuditActions.BranchCreated, nameof(Branch), branch.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(branch);
    }

    public async Task<BranchDto> UpdateAsync(Guid id, SaveBranchRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.BranchNotFound);
        clinic.EnsureBranchAccess(branch.Id);
        db.SetOriginalRowVersion(branch, RowVersions.Decode(req.RowVersion));
        if (branch.IsMain && !req.IsActive) throw new ConflictException(ErrorCodes.MainBranchProtected);
        if (!branch.IsActive && req.IsActive) await limits.EnsureCanAddAsync(LimitKeys.MaxBranches, ct);

        Apply(branch, req);
        audit.Add(AuditActions.BranchUpdated, nameof(Branch), id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(branch);
    }

    private static void Apply(Branch b, SaveBranchRequest r)
    {
        b.Name = r.Name.Trim();
        b.City = string.IsNullOrWhiteSpace(r.City) ? null : r.City.Trim();
        b.Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim();
        b.Phone = SaudiFormats.NormalizeMobile(r.Phone);
        b.Latitude = r.Latitude;
        b.Longitude = r.Longitude;
        b.IsActive = r.IsActive;
    }

    private static BranchDto ToDto(Branch b) =>
        new(b.Id, b.Name, b.City, b.Address, b.Phone, b.Latitude, b.Longitude, b.IsMain, b.IsActive, RowVersions.Encode(b.RowVersion));
}
