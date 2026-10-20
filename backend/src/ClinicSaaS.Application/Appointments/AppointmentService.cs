using System.Globalization;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Appointments;

public sealed record AppointmentDto(
    Guid Id, Guid BranchId, Guid PatientId, string PatientName, string PatientMrn, string PatientMobile,
    Guid DoctorId, string DoctorName, Guid? ServiceId, string? ServiceName, DateTime StartUtc, DateTime EndUtc,
    AppointmentStatus Status, AppointmentSource Source, string? TokenNumber, string? Reason, string? CancellationReason,
    string RowVersion, DateTime? CheckedInAtUtc);

public sealed record AppointmentListQuery : PageQuery
{
    /// <summary>A calendar day in the clinic's time zone (e.g. 2026-10-06). Takes precedence over From/To.</summary>
    public DateOnly? Date { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public Guid? DoctorId { get; init; }
    public Guid? BranchId { get; init; }
    public Guid? PatientId { get; init; }
    public AppointmentStatus? Status { get; init; }
}

public sealed record CreateAppointmentRequest(
    Guid PatientId, Guid DoctorId, Guid? BranchId, Guid? ServiceId, DateTime? StartUtc, int? DurationMinutes,
    AppointmentSource Source = AppointmentSource.Reception, string? Reason = null);

public sealed record RescheduleAppointmentRequest(DateTime StartUtc, Guid? DoctorId, Guid? BranchId, int? DurationMinutes, string RowVersion);

public sealed record ChangeAppointmentStatusRequest(string RowVersion, string? Reason = null);

public sealed record AvailableSlot(DateTime StartUtc, DateTime EndUtc, Guid BranchId, string LocalTime);

public sealed class CreateAppointmentValidator : AbstractValidator<CreateAppointmentRequest>
{
    public CreateAppointmentValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty().WithMessage(ValidationCodes.Required);
        RuleFor(x => x.DoctorId).NotEmpty().WithMessage(ValidationCodes.Required);
        RuleFor(x => x.Source).IsInEnum().WithMessage(ValidationCodes.InvalidValue);
        RuleFor(x => x.StartUtc).NotNull().WithMessage(ValidationCodes.Required).When(x => x.Source != AppointmentSource.WalkIn);
        // A walk-in starts when the patient arrives, so the server sets the time. A client value would let a
        // "walk-in" land on any date while skipping working hours, the past-time check and the overlap guard.
        RuleFor(x => x.StartUtc).Null().WithMessage(ValidationCodes.InvalidValue).When(x => x.Source == AppointmentSource.WalkIn);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, AppointmentService.MaxDurationMinutes).When(x => x.DurationMinutes is not null)
            .WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.Reason).MaxLen(500);
    }
}

public sealed class RescheduleAppointmentValidator : AbstractValidator<RescheduleAppointmentRequest>
{
    public RescheduleAppointmentValidator()
    {
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, AppointmentService.MaxDurationMinutes).When(x => x.DurationMinutes is not null)
            .WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

/// <summary>
/// Booking rules (ported from the legacy system and hardened):
/// 1. A regular appointment must fall inside one of the doctor's working windows at that branch (clinic time).
/// 2. Two slot-occupying appointments of the same doctor may not overlap (half-open intervals). The check runs
///    under a per-doctor application lock inside the transaction, and the filtered unique index
///    UX_Appointments_ActiveSlot is the database backstop.
/// 3. Walk-ins skip both checks (first come, first served) and get a per-day queue token. Their start time is
///    always the server clock; a client-supplied StartUtc is rejected (400, field startUtc).
/// </summary>
public sealed class AppointmentService(
    IAppDbContext db,
    ClinicContext clinic,
    IClock clock,
    IUsageLimiter limits,
    IAuditLogger audit,
    IValidator<CreateAppointmentRequest> createValidator,
    IValidator<RescheduleAppointmentRequest> rescheduleValidator)
{
    public const int MaxDurationMinutes = 480;
    /// <summary>Booking a few minutes "in the past" is allowed for the front desk's convenience.</summary>
    private static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(5);

    // ------------------------------------------------------------------ queries

    public async Task<PagedResult<AppointmentDto>> ListAsync(AppointmentListQuery query, CancellationToken ct)
    {
        var q = db.Appointments.AsNoTracking();
        if (query.Date is { } date)
        {
            var (from, to) = ClinicContext.DayBounds(date, await clinic.GetTimeZoneAsync(ct));
            q = q.Where(a => a.StartUtc >= from && a.StartUtc < to);
        }
        else
        {
            if (query.FromUtc is { } f) q = q.Where(a => a.StartUtc >= f);
            if (query.ToUtc is { } t) q = q.Where(a => a.StartUtc < t);
        }
        if (query.DoctorId is { } d) q = q.Where(a => a.DoctorId == d);
        if (query.BranchId is { } b) q = q.Where(a => a.BranchId == b);
        if (query.PatientId is { } p) q = q.Where(a => a.PatientId == p);
        if (query.Status is { } s) q = q.Where(a => a.Status == s);
        q = RestrictToAllowedBranches(q);

        var total = await q.CountAsync(ct);
        var items = await Project(q.OrderBy(a => a.StartUtc).ThenBy(a => a.TokenNumber)
            .Skip((query.SafePage - 1) * query.SafePageSize).Take(query.SafePageSize)).ToListAsync(ct);
        return new PagedResult<AppointmentDto>(items, query.SafePage, query.SafePageSize, total);
    }

    public async Task<AppointmentDto> GetAsync(Guid id, CancellationToken ct) =>
        await Project(RestrictToAllowedBranches(db.Appointments.AsNoTracking().Where(a => a.Id == id))).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException(ErrorCodes.AppointmentNotFound);

    /// <summary>Free slots for a doctor on a clinic-local day, stepping by the doctor's slot length.</summary>
    public async Task<IReadOnlyList<AvailableSlot>> GetAvailabilityAsync(Guid doctorId, DateOnly date, Guid? branchId, Guid? serviceId, CancellationToken ct)
    {
        var zone = await clinic.GetTimeZoneAsync(ct);
        var doctor = await LoadActiveDoctorAsync(doctorId, ct);
        var service = serviceId is { } sid ? await LoadServiceAsync(sid, ct) : null;
        var duration = TimeSpan.FromMinutes(service?.DurationMinutes ?? doctor.DefaultSlotMinutes);
        var step = TimeSpan.FromMinutes(doctor.DefaultSlotMinutes);

        var windows = doctor.Schedules
            .Where(s => s.DayOfWeek == date.DayOfWeek && (branchId == null || s.BranchId == branchId))
            .Where(s => clinic.RestrictedBranchIds.Count == 0 || clinic.RestrictedBranchIds.Contains(s.BranchId))
            .OrderBy(s => s.StartTime).ToList();
        if (windows.Count == 0) return [];

        var (dayStart, dayEnd) = ClinicContext.DayBounds(date, zone);
        var busy = await OccupyingAppointments(doctorId)
            .Where(a => a.StartUtc < dayEnd && a.EndUtc > dayStart)
            .Select(a => new { a.StartUtc, a.EndUtc })
            .ToListAsync(ct);

        var now = clock.UtcNow;
        var slots = new List<AvailableSlot>();
        foreach (var w in windows)
        {
            // TimeSpan arithmetic (unlike TimeOnly.Add) never wraps past midnight, so the loop always ends.
            for (var offset = w.StartTime.ToTimeSpan(); offset + duration <= w.EndTime.ToTimeSpan(); offset += step)
            {
                var local = TimeOnly.FromTimeSpan(offset);
                var start = ClinicContext.ToUtc(date, local, zone);
                var end = start.Add(duration);
                if (start < now) continue;
                if (busy.Any(b => Appointment.Overlaps(start, end, b.StartUtc, b.EndUtc))) continue;
                slots.Add(new AvailableSlot(start, end, w.BranchId, local.ToString("HH:mm", CultureInfo.InvariantCulture)));
            }
        }
        return slots;
    }

    // ------------------------------------------------------------------ commands

    public async Task<AppointmentDto> CreateAsync(CreateAppointmentRequest req, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(req, ct);
        await limits.EnsureCanAddAsync(LimitKeys.MaxAppointmentsPerMonth, ct);
        var now = clock.UtcNow;

        if (!await db.Patients.AnyAsync(p => p.Id == req.PatientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);
        var doctor = await LoadActiveDoctorAsync(req.DoctorId, ct);
        var service = req.ServiceId is { } sid ? await LoadServiceAsync(sid, ct) : null;
        var duration = TimeSpan.FromMinutes(req.DurationMinutes ?? service?.DurationMinutes ?? doctor.DefaultSlotMinutes);
        var isWalkIn = req.Source == AppointmentSource.WalkIn;
        // Walk-ins always start at the server clock (the validator rejects a client StartUtc).
        var start = isWalkIn ? now : req.StartUtc!.Value;
        start = DateTime.SpecifyKind(start.Kind == DateTimeKind.Local ? start.ToUniversalTime() : start, DateTimeKind.Utc);
        var branchId = await ResolveBranchAsync(req.BranchId, doctor, ct);

        if (!isWalkIn)
        {
            if (start < now - PastGrace) throw new DomainException(ErrorCodes.SlotInPast);
            await EnsureWithinWorkingHoursAsync(doctor, branchId, start, start + duration, ct);
        }

        var appointment = new Appointment
        {
            BranchId = branchId,
            PatientId = req.PatientId,
            DoctorId = doctor.Id,
            ServiceId = service?.Id,
            StartUtc = start,
            EndUtc = start + duration,
            Source = req.Source,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
        };

        await SaveWithSlotGuardAsync(async c =>
        {
            if (isWalkIn)
            {
                appointment.TokenNumber = await NextWalkInTokenAsync(start, c);
            }
            else
            {
                await db.AcquireLockAsync(DoctorLock(doctor.Id), c);
                await EnsureNoOverlapAsync(doctor.Id, appointment.StartUtc, appointment.EndUtc, excludeId: null, c);
            }
            db.Appointments.Add(appointment);
            audit.Add(AuditActions.AppointmentCreated, nameof(Appointment), appointment.Id.ToString(),
                details: new { source = req.Source.ToString() });
            await db.SaveChangesAsync(c);
        }, ct);

        return await GetAsync(appointment.Id, ct);
    }

    public async Task<AppointmentDto> RescheduleAsync(Guid id, RescheduleAppointmentRequest req, CancellationToken ct)
    {
        await rescheduleValidator.ValidateOrThrowAsync(req, ct);
        var appointment = await LoadTrackedAsync(id, ct);
        db.SetOriginalRowVersion(appointment, RowVersions.Decode(req.RowVersion));
        if (appointment.Status is not (AppointmentStatus.Scheduled or AppointmentStatus.Confirmed) || appointment.Source == AppointmentSource.WalkIn)
            throw new DomainException(ErrorCodes.AppointmentNotEditable);

        var doctor = await LoadActiveDoctorAsync(req.DoctorId ?? appointment.DoctorId, ct);
        var branchId = req.BranchId is { } b ? await ResolveBranchAsync(b, doctor, ct) : appointment.BranchId;
        clinic.EnsureBranchAccess(branchId);
        var duration = req.DurationMinutes is { } m ? TimeSpan.FromMinutes(m) : appointment.EndUtc - appointment.StartUtc;
        var start = DateTime.SpecifyKind(req.StartUtc, DateTimeKind.Utc);
        if (start < clock.UtcNow - PastGrace) throw new DomainException(ErrorCodes.SlotInPast);
        await EnsureWithinWorkingHoursAsync(doctor, branchId, start, start + duration, ct);

        var previous = new { appointment.StartUtc, appointment.DoctorId };
        await SaveWithSlotGuardAsync(async c =>
        {
            // Lock both calendars in a stable order so two opposite moves can't deadlock.
            foreach (var lockId in new[] { appointment.DoctorId, doctor.Id }.Distinct().Order())
                await db.AcquireLockAsync(DoctorLock(lockId), c);
            await EnsureNoOverlapAsync(doctor.Id, start, start + duration, excludeId: appointment.Id, c);

            appointment.DoctorId = doctor.Id;
            appointment.BranchId = branchId;
            appointment.StartUtc = start;
            appointment.EndUtc = start + duration;
            audit.Add(AuditActions.AppointmentRescheduled, nameof(Appointment), id.ToString(),
                details: new { fromUtc = previous.StartUtc, toUtc = start, doctorChanged = previous.DoctorId != doctor.Id });
            await db.SaveChangesAsync(c);
        }, ct);

        return await GetAsync(id, ct);
    }

    public async Task<AppointmentDto> ChangeStatusAsync(Guid id, AppointmentStatus target, ChangeAppointmentStatusRequest req, CancellationToken ct)
    {
        var appointment = await LoadTrackedAsync(id, ct);
        db.SetOriginalRowVersion(appointment, RowVersions.Decode(req.RowVersion));
        var now = clock.UtcNow;
        if (target == AppointmentStatus.NoShow && now < appointment.StartUtc) throw new DomainException(ErrorCodes.NoShowBeforeStart);

        var from = appointment.Status;
        appointment.TransitionTo(target, now, req.Reason);
        audit.Add(AuditActions.AppointmentStatusChanged, nameof(Appointment), id.ToString(),
            details: new { from = from.ToString(), to = target.ToString() });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------ rules

    private async Task EnsureWithinWorkingHoursAsync(DoctorProfile doctor, Guid branchId, DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var zone = await clinic.GetTimeZoneAsync(ct);
        var localStart = ClinicContext.ToLocal(startUtc, zone);
        var localEnd = ClinicContext.ToLocal(endUtc, zone);
        var sameDay = localEnd.Date == localStart.Date;
        var covered = sameDay && doctor.Schedules.Any(s =>
            s.BranchId == branchId && s.Covers(localStart.DayOfWeek, TimeOnly.FromDateTime(localStart), TimeOnly.FromDateTime(localEnd)));
        if (!covered) throw new DomainException(ErrorCodes.OutsideWorkingHours);
    }

    private async Task EnsureNoOverlapAsync(Guid doctorId, DateTime start, DateTime end, Guid? excludeId, CancellationToken ct)
    {
        // Lower bound on StartUtc keeps this an index range seek on (TenantId, DoctorId, StartUtc).
        var lower = start.AddMinutes(-MaxDurationMinutes);
        var clash = await OccupyingAppointments(doctorId)
            .Where(a => a.StartUtc < end && a.StartUtc >= lower && a.EndUtc > start)
            .Where(a => excludeId == null || a.Id != excludeId)
            .AnyAsync(ct);
        if (clash) throw new ConflictException(ErrorCodes.SlotTaken);
    }

    private IQueryable<Appointment> OccupyingAppointments(Guid doctorId) =>
        db.Appointments.Where(a => a.DoctorId == doctorId
                                   && Appointment.SlotOccupyingStatuses.Contains(a.Status)
                                   && a.Source != AppointmentSource.WalkIn);

    /// <summary>T-01, T-02… per clinic per local day, serialized by a per-clinic lock.</summary>
    private async Task<string> NextWalkInTokenAsync(DateTime startUtc, CancellationToken ct)
    {
        var zone = await clinic.GetTimeZoneAsync(ct);
        var localDay = DateOnly.FromDateTime(ClinicContext.ToLocal(startUtc, zone));
        await db.AcquireLockAsync(string.Create(CultureInfo.InvariantCulture, $"walkin:{clinic.TenantId:N}:{localDay:yyyyMMdd}"), ct);
        var (from, to) = ClinicContext.DayBounds(localDay, zone);
        var count = await db.Appointments.CountAsync(a => a.Source == AppointmentSource.WalkIn && a.StartUtc >= from && a.StartUtc < to, ct);
        return $"T-{count + 1:D2}";
    }

    /// <summary>Runs the write in a transaction and maps the unique-index backstop to SLOT_TAKEN.</summary>
    private async Task SaveWithSlotGuardAsync(Func<CancellationToken, Task> write, CancellationToken ct)
    {
        try
        {
            await db.InTransactionAsync(async c => { await write(c); return true; }, ct: ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UX_Appointments_ActiveSlot") == true)
        {
            throw new ConflictException(ErrorCodes.SlotTaken);
        }
    }

    private async Task<Guid> ResolveBranchAsync(Guid? requested, DoctorProfile doctor, CancellationToken ct)
    {
        Guid branchId;
        if (requested is { } id)
        {
            if (!await db.Branches.AnyAsync(b => b.Id == id && b.IsActive, ct)) throw new NotFoundException(ErrorCodes.BranchNotFound);
            branchId = id;
        }
        else
        {
            // Unambiguous defaults: the doctor's only branch, else the clinic's only active branch.
            var doctorBranches = doctor.Schedules.Select(s => s.BranchId).Distinct().ToList();
            var candidates = doctorBranches.Count > 0
                ? doctorBranches
                : await db.Branches.Where(b => b.IsActive).Select(b => b.Id).Take(2).ToListAsync(ct);
            if (candidates.Count != 1)
                throw new RequestValidationException(new Dictionary<string, string[]> { ["branchId"] = [ValidationCodes.Required] });
            branchId = candidates[0];
        }
        clinic.EnsureBranchAccess(branchId);
        return branchId;
    }

    private async Task<DoctorProfile> LoadActiveDoctorAsync(Guid id, CancellationToken ct)
    {
        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Schedules).FirstOrDefaultAsync(d => d.Id == id, ct)
                     ?? throw new NotFoundException(ErrorCodes.DoctorNotFound);
        if (!doctor.IsActive) throw new DomainException(ErrorCodes.DoctorInactive);
        return doctor;
    }

    private async Task<ClinicService> LoadServiceAsync(Guid id, CancellationToken ct) =>
        await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.IsActive, ct)
        ?? throw new NotFoundException(ErrorCodes.ServiceNotFound);

    private async Task<Appointment> LoadTrackedAsync(Guid id, CancellationToken ct) =>
        await RestrictToAllowedBranches(db.Appointments).FirstOrDefaultAsync(a => a.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.AppointmentNotFound);

    /// <summary>Branch-restricted members see only their branches; other branches' rows are "not found".</summary>
    private IQueryable<Appointment> RestrictToAllowedBranches(IQueryable<Appointment> q)
    {
        var allowed = clinic.RestrictedBranchIds;
        return allowed.Count == 0 ? q : q.Where(a => allowed.Contains(a.BranchId));
    }

    private static string DoctorLock(Guid doctorId) => $"appt:{doctorId:N}";

    /// <summary>
    /// Joins display names. Soft-deleted patients/doctors/services still show in history, so only the
    /// soft-delete filter is lifted — the tenant filter stays on every table.
    /// </summary>
    private IQueryable<AppointmentDto> Project(IQueryable<Appointment> q) =>
        from a in q.IgnoreQueryFilters([QueryFilters.SoftDelete])
        join p in db.Patients on a.PatientId equals p.Id
        join d in db.Doctors on a.DoctorId equals d.Id
        join s in db.Services on a.ServiceId equals (Guid?)s.Id into services
        from s in services.DefaultIfEmpty()
        select new AppointmentDto(a.Id, a.BranchId, a.PatientId, p.FullName, p.MedicalRecordNumber, p.MobileNumber,
            a.DoctorId, d.DisplayNameAr, a.ServiceId, s != null ? s.NameAr : null, a.StartUtc, a.EndUtc, a.Status, a.Source,
            a.TokenNumber, a.Reason, a.CancellationReason, Convert.ToBase64String(a.RowVersion), a.CheckedInAtUtc);
}
