using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Doctors;

public sealed record ScheduleWindowDto(Guid BranchId, DayOfWeek DayOfWeek, TimeOnly StartTime, TimeOnly EndTime);

public sealed record DoctorDto(
    Guid Id, Guid MembershipId, string DisplayNameAr, string? DisplayNameEn, string Specialty, string? LicenseNumber,
    string? Bio, decimal ConsultationFee, int DefaultSlotMinutes, bool AcceptsOnlineBooking, bool IsActive,
    IReadOnlyList<ScheduleWindowDto> Schedule, string RowVersion);

public sealed record DoctorListQuery : PageQuery
{
    public bool? ActiveOnly { get; init; }
    public Guid? BranchId { get; init; }
}

public record SaveDoctorRequest(
    string DisplayNameAr, string? DisplayNameEn, string Specialty, string? LicenseNumber, string? Bio,
    decimal ConsultationFee, int DefaultSlotMinutes, bool AcceptsOnlineBooking);

public sealed record CreateDoctorRequest(
    Guid MembershipId, string DisplayNameAr, string? DisplayNameEn, string Specialty, string? LicenseNumber, string? Bio,
    decimal ConsultationFee, int DefaultSlotMinutes, bool AcceptsOnlineBooking, IReadOnlyList<ScheduleWindowDto>? Schedule)
    : SaveDoctorRequest(DisplayNameAr, DisplayNameEn, Specialty, LicenseNumber, Bio, ConsultationFee, DefaultSlotMinutes, AcceptsOnlineBooking);

public sealed record UpdateDoctorRequest(
    string DisplayNameAr, string? DisplayNameEn, string Specialty, string? LicenseNumber, string? Bio,
    decimal ConsultationFee, int DefaultSlotMinutes, bool AcceptsOnlineBooking, bool IsActive, string RowVersion)
    : SaveDoctorRequest(DisplayNameAr, DisplayNameEn, Specialty, LicenseNumber, Bio, ConsultationFee, DefaultSlotMinutes, AcceptsOnlineBooking);

public sealed record UpdateScheduleRequest(IReadOnlyList<ScheduleWindowDto> Windows, string RowVersion);

public sealed class SaveDoctorValidator : AbstractValidator<SaveDoctorRequest>
{
    public SaveDoctorValidator()
    {
        RuleFor(x => x.DisplayNameAr).RequiredText().MaxLen(200);
        RuleFor(x => x.DisplayNameEn).MaxLen(200);
        RuleFor(x => x.Specialty).RequiredText().MaxLen(100);
        RuleFor(x => x.LicenseNumber).MaxLen(50);
        RuleFor(x => x.Bio).MaxLen(1000);
        RuleFor(x => x.ConsultationFee).InclusiveBetween(0, 100_000).WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.DefaultSlotMinutes).InclusiveBetween(5, 240).WithMessage(ValidationCodes.OutOfRange);
    }
}

public sealed class CreateDoctorValidator : AbstractValidator<CreateDoctorRequest>
{
    public CreateDoctorValidator()
    {
        Include(new SaveDoctorValidator());
        RuleFor(x => x.MembershipId).NotEmpty().WithMessage(ValidationCodes.Required);
    }
}

public sealed class UpdateDoctorValidator : AbstractValidator<UpdateDoctorRequest>
{
    public UpdateDoctorValidator()
    {
        Include(new SaveDoctorValidator());
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

public sealed class UpdateScheduleValidator : AbstractValidator<UpdateScheduleRequest>
{
    public UpdateScheduleValidator()
    {
        RuleFor(x => x.Windows).NotNull().WithMessage(ValidationCodes.Required)
            .Must(w => w is null || w.Count <= 50).WithMessage(ValidationCodes.OutOfRange);
        RuleForEach(x => x.Windows).Must(w => w.EndTime > w.StartTime && Enum.IsDefined(w.DayOfWeek))
            .WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

/// <summary>
/// Doctors and their weekly working hours. Branch context (F2, T-011) for members restricted to branches:
/// - A doctor is visible only if they have at least one schedule window in an allowed branch, or no schedule at all
///   (not tied to any branch yet, so a branch manager can onboard them). Others are "not found" and left out of lists.
/// - Only windows in allowed branches are returned; other branches' hours are never disclosed.
/// - Schedule updates replace only the windows in the member's branches and preserve the rest (requests naming another
///   branch: 403 BRANCH_ACCESS_DENIED). Overlap is still checked against the preserved windows.
/// - Profile edits (incl. deactivation) and windows on create are limited to doctors/branches entirely inside the
///   member's branches, because they affect every branch the doctor works in.
/// Unrestricted members are unaffected.
/// </summary>
public sealed class DoctorService(
    IAppDbContext db,
    ClinicContext clinic,
    IUsageLimiter limits,
    IClock clock,
    IAuditLogger audit,
    IValidator<CreateDoctorRequest> createValidator,
    IValidator<UpdateDoctorRequest> updateValidator,
    IValidator<UpdateScheduleRequest> scheduleValidator)
{
    private IReadOnlyList<Guid> Allowed => clinic.RestrictedBranchIds;
    private bool IsRestricted => Allowed.Count > 0;

    public async Task<PagedResult<DoctorDto>> ListAsync(DoctorListQuery query, CancellationToken ct)
    {
        var q = VisibleDoctors(db.Doctors.AsNoTracking());
        if (query.ActiveOnly == true) q = q.Where(d => d.IsActive);
        if (query.BranchId is { } b) q = q.Where(d => d.Schedules.Any(s => s.BranchId == b));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(d => d.DisplayNameAr.StartsWith(term) || (d.DisplayNameEn != null && d.DisplayNameEn.StartsWith(term)) || d.Specialty.StartsWith(term));
        }
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(d => d.DisplayNameAr).Skip((query.SafePage - 1) * query.SafePageSize).Take(query.SafePageSize)
            .Include(d => d.Schedules).ToListAsync(ct);
        return new PagedResult<DoctorDto>(items.Select(ToDto).ToList(), query.SafePage, query.SafePageSize, total);
    }

    public async Task<DoctorDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, tracked: false, ct));

    public async Task<DoctorDto> CreateAsync(CreateDoctorRequest req, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(req, ct);
        await limits.EnsureCanAddAsync(LimitKeys.MaxDoctors, ct);

        // The membership must be an active seat in this clinic (tenant filter turns a foreign id into "not found").
        if (!await db.TenantMemberships.AnyAsync(m => m.Id == req.MembershipId && m.Status != MembershipStatus.Disabled, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["membershipId"] = [ValidationCodes.InvalidValue] });
        if (await db.Doctors.AnyAsync(d => d.MembershipId == req.MembershipId, ct))
            throw new ConflictException(ErrorCodes.DoctorProfileExists);

        var doctor = new DoctorProfile { MembershipId = req.MembershipId, DisplayNameAr = "", Specialty = "" };
        Apply(doctor, req);
        if (req.Schedule is { Count: > 0 } windows)
        {
            EnsureWindowsInAllowedBranches(windows);
            await ValidateWindowsAsync(windows, windows, ct);
            foreach (var w in windows) doctor.Schedules.Add(ToEntity(w));
        }
        db.Doctors.Add(doctor);
        audit.Add(AuditActions.DoctorCreated, nameof(DoctorProfile), doctor.Id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(doctor);
    }

    public async Task<DoctorDto> UpdateAsync(Guid id, UpdateDoctorRequest req, CancellationToken ct)
    {
        await updateValidator.ValidateOrThrowAsync(req, ct);
        var doctor = await LoadAsync(id, tracked: true, ct);
        // The profile (and the active flag) is shared by every branch the doctor works in.
        if (IsRestricted && doctor.Schedules.Any(s => !Allowed.Contains(s.BranchId)))
            throw new ForbiddenException(ErrorCodes.BranchAccessDenied);
        db.SetOriginalRowVersion(doctor, RowVersions.Decode(req.RowVersion));
        if (!doctor.IsActive && req.IsActive) await limits.EnsureCanAddAsync(LimitKeys.MaxDoctors, ct);

        Apply(doctor, req);
        doctor.IsActive = req.IsActive;
        audit.Add(AuditActions.DoctorUpdated, nameof(DoctorProfile), id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDto(doctor);
    }

    /// <summary>
    /// Replaces the weekly working hours (for a branch-restricted member: only those in their branches; windows in
    /// other branches are preserved). Existing appointments are not touched.
    /// </summary>
    public async Task<DoctorDto> UpdateScheduleAsync(Guid id, UpdateScheduleRequest req, CancellationToken ct)
    {
        await scheduleValidator.ValidateOrThrowAsync(req, ct);
        var doctor = await LoadAsync(id, tracked: true, ct);
        db.SetOriginalRowVersion(doctor, RowVersions.Decode(req.RowVersion));
        EnsureWindowsInAllowedBranches(req.Windows);

        var replaced = doctor.Schedules.Where(s => !IsRestricted || Allowed.Contains(s.BranchId)).ToList();
        var preserved = doctor.Schedules.Except(replaced)
            .Select(s => new ScheduleWindowDto(s.BranchId, s.DayOfWeek, s.StartTime, s.EndTime)).ToList();
        await ValidateWindowsAsync(req.Windows, [.. preserved, .. req.Windows], ct);

        foreach (var s in replaced) doctor.Schedules.Remove(s);
        foreach (var w in req.Windows) doctor.Schedules.Add(ToEntity(w));
        doctor.UpdatedAtUtc = clock.UtcNow; // bump rowversion with child-only changes
        audit.Add(AuditActions.ScheduleUpdated, nameof(DoctorProfile), id.ToString(), details: new { windows = req.Windows.Count });
        await db.SaveChangesAsync(ct);
        return ToDto(doctor);
    }

    private void EnsureWindowsInAllowedBranches(IEnumerable<ScheduleWindowDto> windows)
    {
        if (IsRestricted && windows.Any(w => !Allowed.Contains(w.BranchId)))
            throw new ForbiddenException(ErrorCodes.BranchAccessDenied);
    }

    /// <param name="requested">Windows sent by the caller: their branches must exist and be active.</param>
    /// <param name="resulting">The full resulting schedule: no two windows may overlap.</param>
    private async Task ValidateWindowsAsync(IReadOnlyList<ScheduleWindowDto> requested, IReadOnlyList<ScheduleWindowDto> resulting, CancellationToken ct)
    {
        // A doctor can't be in two places (or two windows) at once, across branches too.
        foreach (var day in resulting.GroupBy(w => w.DayOfWeek))
        {
            var ordered = day.OrderBy(w => w.StartTime).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].StartTime < ordered[i - 1].EndTime) throw new ConflictException(ErrorCodes.ScheduleOverlap);
        }

        var branchIds = requested.Select(w => w.BranchId).Distinct().ToList();
        var found = await db.Branches.CountAsync(b => branchIds.Contains(b.Id) && b.IsActive, ct);
        if (found != branchIds.Count)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["windows"] = [ValidationCodes.InvalidValue] });
    }

    private async Task<DoctorProfile> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var q = VisibleDoctors(db.Doctors).Include(d => d.Schedules).AsQueryable();
        if (!tracked) q = q.AsNoTracking();
        return await q.FirstOrDefaultAsync(d => d.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.DoctorNotFound);
    }

    /// <summary>Branch-restricted members see doctors working in one of their branches, or not scheduled anywhere.</summary>
    private IQueryable<DoctorProfile> VisibleDoctors(IQueryable<DoctorProfile> q) => clinic.VisibleDoctors(q);

    private static void Apply(DoctorProfile d, SaveDoctorRequest r)
    {
        d.DisplayNameAr = r.DisplayNameAr.Trim();
        d.DisplayNameEn = string.IsNullOrWhiteSpace(r.DisplayNameEn) ? null : r.DisplayNameEn.Trim();
        d.Specialty = r.Specialty.Trim();
        d.LicenseNumber = string.IsNullOrWhiteSpace(r.LicenseNumber) ? null : r.LicenseNumber.Trim();
        d.Bio = string.IsNullOrWhiteSpace(r.Bio) ? null : r.Bio.Trim();
        d.ConsultationFee = r.ConsultationFee;
        d.DefaultSlotMinutes = r.DefaultSlotMinutes;
        d.AcceptsOnlineBooking = r.AcceptsOnlineBooking;
    }

    private static DoctorSchedule ToEntity(ScheduleWindowDto w) =>
        new() { BranchId = w.BranchId, DayOfWeek = w.DayOfWeek, StartTime = w.StartTime, EndTime = w.EndTime };

    /// <summary>Other branches' windows are left out for branch-restricted members.</summary>
    private DoctorDto ToDto(DoctorProfile d) => new(d.Id, d.MembershipId, d.DisplayNameAr, d.DisplayNameEn, d.Specialty,
        d.LicenseNumber, d.Bio, d.ConsultationFee, d.DefaultSlotMinutes, d.AcceptsOnlineBooking, d.IsActive,
        d.Schedules.Where(s => !IsRestricted || Allowed.Contains(s.BranchId))
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .Select(s => new ScheduleWindowDto(s.BranchId, s.DayOfWeek, s.StartTime, s.EndTime)).ToList(),
        RowVersions.Encode(d.RowVersion));
}

// ============================================================ services catalog

public sealed record ClinicServiceDto(Guid Id, string NameAr, string? NameEn, int DurationMinutes, decimal Price, bool IsActive, bool IsBookableOnline, string RowVersion);

public sealed record SaveClinicServiceRequest(string NameAr, string? NameEn, int DurationMinutes, decimal Price, bool IsActive = true, bool IsBookableOnline = true, string? RowVersion = null);

public sealed class SaveClinicServiceValidator : AbstractValidator<SaveClinicServiceRequest>
{
    public SaveClinicServiceValidator()
    {
        RuleFor(x => x.NameAr).RequiredText().MaxLen(200);
        RuleFor(x => x.NameEn).MaxLen(200);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, 480).WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.Price).InclusiveBetween(0, 1_000_000).WithMessage(ValidationCodes.OutOfRange);
    }
}

public sealed class ClinicServiceCatalog(IAppDbContext db, IAuditLogger audit, IValidator<SaveClinicServiceRequest> validator)
{
    public async Task<IReadOnlyList<ClinicServiceDto>> ListAsync(bool activeOnly, CancellationToken ct) =>
        (await db.Services.AsNoTracking().Where(s => !activeOnly || s.IsActive).OrderBy(s => s.NameAr).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<ClinicServiceDto> CreateAsync(SaveClinicServiceRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        var s = new ClinicService { NameAr = "" };
        Apply(s, req);
        db.Services.Add(s);
        audit.Add(AuditActions.ServiceChanged, nameof(ClinicService), s.Id.ToString(), details: new { op = "create" });
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    public async Task<ClinicServiceDto> UpdateAsync(Guid id, SaveClinicServiceRequest req, CancellationToken ct)
    {
        await validator.ValidateOrThrowAsync(req, ct);
        var s = await db.Services.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.ServiceNotFound);
        db.SetOriginalRowVersion(s, RowVersions.Decode(req.RowVersion));
        Apply(s, req);
        audit.Add(AuditActions.ServiceChanged, nameof(ClinicService), id.ToString(), details: new { op = "update" });
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Services.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException(ErrorCodes.ServiceNotFound);
        db.Services.Remove(s); // soft delete — past appointments keep their reference
        audit.Add(AuditActions.ServiceChanged, nameof(ClinicService), id.ToString(), details: new { op = "delete" });
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(ClinicService s, SaveClinicServiceRequest r)
    {
        s.NameAr = r.NameAr.Trim();
        s.NameEn = string.IsNullOrWhiteSpace(r.NameEn) ? null : r.NameEn.Trim();
        s.DurationMinutes = r.DurationMinutes;
        s.Price = r.Price;
        s.IsActive = r.IsActive;
        s.IsBookableOnline = r.IsBookableOnline;
    }

    private static ClinicServiceDto ToDto(ClinicService s) =>
        new(s.Id, s.NameAr, s.NameEn, s.DurationMinutes, s.Price, s.IsActive, s.IsBookableOnline, RowVersions.Encode(s.RowVersion));
}
