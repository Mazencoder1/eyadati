using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Common;

/// <summary>
/// The current clinic for this request (loaded once) plus its local-time helpers. All instants are stored in
/// UTC; this is the only place that converts to/from the clinic's wall clock (Asia/Riyadh by default).
/// </summary>
public sealed class ClinicContext(IAppDbContext db, ITenantContext tenant, ICurrentUser user)
{
    private Tenant? _tenant;
    private TimeZoneInfo? _zone;

    public Guid TenantId => tenant.TenantId ?? throw new ForbiddenException(ErrorCodes.TenantRequired);

    public async Task<Tenant> GetTenantAsync(CancellationToken ct)
    {
        if (_tenant is not null) return _tenant;
        var id = TenantId;
        _tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.TenantNotFound);
        return _tenant;
    }

    public async Task<TimeZoneInfo> GetTimeZoneAsync(CancellationToken ct)
    {
        if (_zone is not null) return _zone;
        var t = await GetTenantAsync(ct);
        _zone = TimeZoneInfo.TryFindSystemTimeZoneById(t.TimeZone, out var z) ? z : TimeZoneInfo.FindSystemTimeZoneById(Tenant.DefaultTimeZone);
        return _zone;
    }

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), zone);

    /// <summary>UTC bounds [start, end) of a local calendar day.</summary>
    public static (DateTime StartUtc, DateTime EndUtc) DayBounds(DateOnly date, TimeZoneInfo zone) =>
        (ToUtc(date, TimeOnly.MinValue, zone), ToUtc(date.AddDays(1), TimeOnly.MinValue, zone));

    private bool _ownDoctorResolved;
    private Guid? _ownDoctorId;

    /// <summary>
    /// Clinical-record scoping (T-012): if the caller's seat in this clinic has a doctor profile and the caller lacks
    /// MedicalRecords.ViewAllDoctors, returns that profile's id — visits and prescriptions are then limited to it.
    /// Null = no such limit (non-doctor users keep the plain permission rules). Soft-deleted profiles count too, so
    /// removing a doctor profile never widens what that person sees.
    /// </summary>
    public async Task<Guid?> OwnDoctorScopeAsync(CancellationToken ct)
    {
        if (_ownDoctorResolved) return _ownDoctorId;
        if (!user.HasPermission(Domain.Authorization.Permissions.MedicalRecords.ViewAllDoctors) && user.MembershipId is { } membershipId)
        {
            _ownDoctorId = await db.Doctors.IgnoreQueryFilters([QueryFilters.SoftDelete])
                .Where(d => d.MembershipId == membershipId).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        }
        _ownDoctorResolved = true;
        return _ownDoctorId;
    }

    private bool _callerDoctorResolved;
    private Guid? _callerDoctorId;

    /// <summary>
    /// The doctor profile of the signed-in caller's seat in this clinic (not deleted), or null when the caller is not
    /// a doctor here. Derived from the token's membership only — never from anything the client sends.
    /// </summary>
    public async Task<Guid?> CallerDoctorIdAsync(CancellationToken ct)
    {
        if (_callerDoctorResolved) return _callerDoctorId;
        if (user.MembershipId is { } membershipId)
            _callerDoctorId = await db.Doctors.Where(d => d.MembershipId == membershipId).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        _callerDoctorResolved = true;
        return _callerDoctorId;
    }

    /// <summary>
    /// Branch scoping: members restricted to specific branches may only act on those. Throws 403 otherwise.
    /// </summary>
    public void EnsureBranchAccess(Guid branchId)
    {
        if (user.BranchIds.Count > 0 && !user.BranchIds.Contains(branchId))
            throw new ForbiddenException(ErrorCodes.BranchAccessDenied);
    }

    public IReadOnlyList<Guid> RestrictedBranchIds => user.BranchIds;

    /// <summary>
    /// T-011: branch-restricted members see doctors working in one of their branches, or not scheduled anywhere.
    /// Every path that resolves a doctor for such a member (the doctors API, recording a visit) uses this rule, so a
    /// doctor hidden on /doctors is "not found" everywhere (QA-16).
    /// </summary>
    public IQueryable<DoctorProfile> VisibleDoctors(IQueryable<DoctorProfile> q)
    {
        var allowed = RestrictedBranchIds;
        if (allowed.Count == 0) return q;
        return q.Where(d => !d.Schedules.Any() || d.Schedules.Any(s => allowed.Contains(s.BranchId)));
    }
}

public static class RowVersions
{
    public static byte[] Decode(string? value)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(value)) return Convert.FromBase64String(value);
        }
        catch (FormatException) { }
        throw new RequestValidationException(new Dictionary<string, string[]> { ["rowVersion"] = [ValidationCodes.InvalidValue] });
    }

    public static string Encode(byte[] value) => Convert.ToBase64String(value);
}
