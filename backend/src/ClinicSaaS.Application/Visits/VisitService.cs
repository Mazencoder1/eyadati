using System.Linq.Expressions;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Visits;

public sealed record VisitVitals(
    int? SystolicBp, int? DiastolicBp, int? HeartRate, int? RespiratoryRate, decimal? TemperatureC,
    int? OxygenSaturation, decimal? WeightKg, decimal? HeightCm);

/// <summary>
/// The clinical content of a visit. Returned only to callers holding MedicalRecords.View; written only by callers
/// holding MedicalRecords.Edit. Never logged or audited.
/// </summary>
public sealed record VisitClinicalData(
    string? Symptoms, string? Diagnosis, string? DiagnosisCode, string? ClinicalNotes, string? TreatmentPlan, VisitVitals? Vitals);

/// <summary>A visit. <see cref="Clinical"/> is null unless the caller holds MedicalRecords.View.</summary>
public sealed record VisitDto(
    Guid Id, Guid BranchId, Guid PatientId, string PatientName, string PatientMrn, Guid DoctorId, string DoctorName,
    Guid? AppointmentId, DateTime VisitedAtUtc, DateOnly? FollowUpDate, string? FollowUpNotes, VisitClinicalData? Clinical,
    DateTime CreatedAtUtc, DateTime? UpdatedAtUtc, string RowVersion,
    VisitStatus Status, DateTime? CompletedAtUtc, Guid? CompletedByUserId);

/// <summary>A visit in a list. <see cref="Diagnosis"/> is null unless the caller holds MedicalRecords.View.</summary>
public sealed record VisitListItem(
    Guid Id, Guid BranchId, Guid PatientId, string PatientName, string PatientMrn, Guid DoctorId, string DoctorName,
    Guid? AppointmentId, DateTime VisitedAtUtc, DateOnly? FollowUpDate, string? Diagnosis,
    VisitStatus Status, DateTime? CompletedAtUtc);

public sealed record VisitListQuery : PageQuery
{
    public Guid? PatientId { get; init; }
    public Guid? DoctorId { get; init; }
    public Guid? BranchId { get; init; }
    public Guid? AppointmentId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}

/// <param name="BranchId">Optional: taken from the appointment when one is given, else the only branch available.</param>
/// <param name="VisitedAtUtc">Optional: defaults to the server clock. May be in the past (late documentation), never in the future.</param>
public sealed record CreateVisitRequest(
    Guid PatientId, Guid DoctorId, Guid? BranchId, Guid? AppointmentId, DateTime? VisitedAtUtc,
    VisitClinicalData? Clinical, DateOnly? FollowUpDate, string? FollowUpNotes);

/// <summary>
/// Patient, doctor, branch and appointment of a visit are fixed once recorded. <see cref="Clinical"/> null = leave
/// the clinical content unchanged (so a caller who cannot see it can never wipe it); non-null = replace it.
/// Follow-up fields are always replaced. <see cref="VisitedAtUtc"/> null = unchanged.
/// </summary>
public sealed record UpdateVisitRequest(
    DateTime? VisitedAtUtc, VisitClinicalData? Clinical, DateOnly? FollowUpDate, string? FollowUpNotes, string RowVersion);

/// <summary>Completing a visit (T-013b): only the row version the caller last saw.</summary>
public sealed record CompleteVisitRequest(string RowVersion);

public sealed class CompleteVisitValidator : AbstractValidator<CompleteVisitRequest>
{
    public CompleteVisitValidator() => RuleFor(x => x.RowVersion).RequiredText();
}

public sealed class VisitClinicalValidator : AbstractValidator<VisitClinicalData>
{
    public VisitClinicalValidator()
    {
        RuleFor(x => x.Symptoms).MaxLen(1000);
        RuleFor(x => x.Diagnosis).MaxLen(1000);
        RuleFor(x => x.DiagnosisCode).MaxLen(20)
            .Matches(@"^\s*[A-Za-z][0-9][0-9A-Za-z](\.[0-9A-Za-z]{1,4})?\s*$").WithMessage(ValidationCodes.InvalidValue)
            .When(x => !string.IsNullOrWhiteSpace(x.DiagnosisCode));
        RuleFor(x => x.ClinicalNotes).MaxLen(4000);
        RuleFor(x => x.TreatmentPlan).MaxLen(2000);
        // Same bounds as CK_Visits_Bp / CK_Visits_Spo2 (so bad input is a 400, not a constraint error); the other
        // ranges are physiological plausibility limits that also fit the column precision.
        When(x => x.Vitals is not null, () =>
        {
            RuleFor(x => x.Vitals!.SystolicBp).InclusiveBetween(40, 300).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.DiastolicBp).InclusiveBetween(20, 200).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.OxygenSaturation).InclusiveBetween(40, 100).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.HeartRate).InclusiveBetween(20, 300).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.RespiratoryRate).InclusiveBetween(4, 80).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.TemperatureC).InclusiveBetween(30m, 45m).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.WeightKg).InclusiveBetween(0.2m, 500m).WithMessage(ValidationCodes.OutOfRange);
            RuleFor(x => x.Vitals!.HeightCm).InclusiveBetween(20m, 300m).WithMessage(ValidationCodes.OutOfRange);
        });
    }
}

public sealed class CreateVisitValidator : AbstractValidator<CreateVisitRequest>
{
    public CreateVisitValidator(IClock clock)
    {
        RuleFor(x => x.PatientId).NotEmpty().WithMessage(ValidationCodes.Required);
        RuleFor(x => x.DoctorId).NotEmpty().WithMessage(ValidationCodes.Required);
        RuleFor(x => x.VisitedAtUtc).Must(v => v is null || VisitService.NotInFuture(v.Value, clock.UtcNow))
            .WithMessage(ValidationCodes.InvalidDate);
        RuleFor(x => x.Clinical!).SetValidator(new VisitClinicalValidator()).When(x => x.Clinical is not null);
        RuleFor(x => x.FollowUpNotes).MaxLen(500);
    }
}

public sealed class UpdateVisitValidator : AbstractValidator<UpdateVisitRequest>
{
    public UpdateVisitValidator(IClock clock)
    {
        RuleFor(x => x.VisitedAtUtc).Must(v => v is null || VisitService.NotInFuture(v.Value, clock.UtcNow))
            .WithMessage(ValidationCodes.InvalidDate);
        RuleFor(x => x.Clinical!).SetValidator(new VisitClinicalValidator()).When(x => x.Clinical is not null);
        RuleFor(x => x.FollowUpNotes).MaxLen(500);
        RuleFor(x => x.RowVersion).RequiredText();
    }
}

/// <summary>
/// Visits (clinical encounters). Rules:
/// - Tenant isolation comes from the data layer: a patient, doctor, branch, appointment or visit of another clinic
///   is simply "not found". The database's composite FKs are the backstop.
/// - Branch isolation works like appointments: branch-restricted members only see visits of their branches, and
///   cannot record a visit in another branch.
/// - Visits.View/Create/Edit gate the endpoints. The clinical content additionally needs MedicalRecords.View to read
///   and MedicalRecords.Edit to write — the same split as the patient's medical block.
/// - A visit for an appointment must match the appointment's patient, doctor and branch, the appointment must be
///   checked in (or completed), and only one active visit per appointment exists (UX_Visits_Appointment).
/// - Own records (T-012): a user whose seat has a doctor profile and who lacks MedicalRecords.ViewAllDoctors sees,
///   edits and records only visits where DoctorId is their own profile; other doctors' visits are "not found".
/// - Clinical authorship (GD-01): only the visit's own doctor — the caller whose seat holds the visit's doctor profile —
///   records, edits or completes it. MedicalRecords.ViewAllDoctors widens what a member may READ, never whose name
///   they write in; a manager, owner or another doctor gets 403 VISIT_NOT_TREATING_DOCTOR.
/// - Recording or editing a visit never changes the appointment's status; only the explicit Complete action does
///   (T-013b), and a completed visit can no longer be edited.
/// </summary>
public sealed class VisitService(
    IAppDbContext db,
    ClinicContext clinic,
    ICurrentUser user,
    IClock clock,
    IAuditLogger audit,
    IValidator<CreateVisitRequest> createValidator,
    IValidator<UpdateVisitRequest> updateValidator,
    IValidator<CompleteVisitRequest> completeValidator)
{
    /// <summary>Tolerance for clock skew between the client and the server.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, Expression<Func<Visit, object>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["visitedAt"] = v => v.VisitedAtUtc,
        ["createdAt"] = v => v.CreatedAtUtc,
    };

    internal static bool NotInFuture(DateTime value, DateTime nowUtc) => ToUtc(value) <= nowUtc + FutureTolerance;

    private bool CanReadClinical => user.HasPermission(Permissions.MedicalRecords.View);

    // ------------------------------------------------------------------ queries

    public async Task<PagedResult<VisitListItem>> ListAsync(VisitListQuery query, CancellationToken ct)
    {
        // Lift only the soft-delete filter (so deleted patients/doctors keep their names in history) and re-apply
        // it to visits explicitly. The tenant filter stays on every table, including the name subqueries.
        var q = db.Visits.AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete]).Where(v => !v.IsDeleted);
        if (query.PatientId is { } p) q = q.Where(v => v.PatientId == p);
        if (query.DoctorId is { } d) q = q.Where(v => v.DoctorId == d);
        if (query.BranchId is { } b) q = q.Where(v => v.BranchId == b);
        if (query.AppointmentId is { } a) q = q.Where(v => v.AppointmentId == a);
        if (query.FromUtc is { } f) { var from = ToUtc(f); q = q.Where(v => v.VisitedAtUtc >= from); }
        if (query.ToUtc is { } t) { var to = ToUtc(t); q = q.Where(v => v.VisitedAtUtc < to); }
        q = await VisibleAsync(q, ct);

        return await q.ApplySort(query.Sort, Sorts, "-visitedAt").ToPagedResultAsync(query, ListProjection(CanReadClinical), ct);
    }

    public async Task<PagedResult<VisitListItem>> ListForPatientAsync(Guid patientId, VisitListQuery query, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);
        return await ListAsync(query with { PatientId = patientId }, ct);
    }

    public async Task<VisitDto> GetAsync(Guid id, CancellationToken ct)
    {
        var dto = await LoadDtoAsync(id, ct);
        // Who opened which record — never what it says.
        audit.Add(AuditActions.VisitViewed, nameof(Visit), id.ToString(), details: dto.Clinical is null ? null : new { clinical = true });
        await db.SaveChangesAsync(ct);
        return dto;
    }

    // ------------------------------------------------------------------ commands

    public async Task<VisitDto> CreateAsync(CreateVisitRequest req, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(req, ct);
        if (req.Clinical is not null) EnsureCanWriteClinical();

        if (!await db.Patients.AnyAsync(p => p.Id == req.PatientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);
        // T-012: a doctor without ViewAllDoctors records visits only in their own name; another doctor is "not found".
        if (await clinic.OwnDoctorScopeAsync(ct) is { } ownDoctor && ownDoctor != req.DoctorId)
            throw new NotFoundException(ErrorCodes.DoctorNotFound);
        // QA-16: a doctor hidden from a branch-restricted member (T-011) is "not found" here too, as on /doctors.
        var doctor = await clinic.VisibleDoctors(db.Doctors.AsNoTracking()).FirstOrDefaultAsync(d => d.Id == req.DoctorId, ct)
                     ?? throw new NotFoundException(ErrorCodes.DoctorNotFound);
        if (!doctor.IsActive) throw new DomainException(ErrorCodes.DoctorInactive);
        await EnsureTreatingDoctorAsync(doctor.Id, ct);

        Guid branchId;
        if (req.AppointmentId is { } appointmentId)
        {
            var appointment = await RestrictToAllowedBranches(db.Appointments.AsNoTracking())
                                  .FirstOrDefaultAsync(a => a.Id == appointmentId, ct)
                              ?? throw new NotFoundException(ErrorCodes.AppointmentNotFound);
            var errors = new Dictionary<string, string[]>();
            if (appointment.PatientId != req.PatientId || appointment.DoctorId != req.DoctorId)
                errors["appointmentId"] = [ValidationCodes.InvalidValue];
            if (req.BranchId is { } requested && requested != appointment.BranchId)
                errors["branchId"] = [ValidationCodes.InvalidValue];
            if (errors.Count > 0) throw new RequestValidationException(errors);
            if (!Visit.RecordableAppointmentStatuses.Contains(appointment.Status))
                throw new DomainException(ErrorCodes.AppointmentNotCheckedIn);
            if (await db.Visits.AnyAsync(v => v.AppointmentId == appointmentId, ct))
                throw new ConflictException(ErrorCodes.VisitAlreadyRecorded);
            branchId = appointment.BranchId;
        }
        else
        {
            branchId = await ResolveBranchAsync(req.BranchId, ct);
        }
        clinic.EnsureBranchAccess(branchId);

        var visit = new Visit
        {
            BranchId = branchId,
            PatientId = req.PatientId,
            DoctorId = doctor.Id,
            AppointmentId = req.AppointmentId,
            VisitedAtUtc = req.VisitedAtUtc is { } at ? ToUtc(at) : clock.UtcNow,
            FollowUpDate = req.FollowUpDate,
            FollowUpNotes = Clean(req.FollowUpNotes),
        };
        if (req.Clinical is { } clinical) ApplyClinical(visit, clinical);

        db.Visits.Add(visit);
        audit.Add(AuditActions.VisitRecorded, nameof(Visit), visit.Id.ToString(),
            details: new { appointment = req.AppointmentId is not null, clinical = req.Clinical is not null });
        await SaveWithAppointmentGuardAsync(ct);
        return await LoadDtoAsync(visit.Id, ct);
    }

    public async Task<VisitDto> UpdateAsync(Guid id, UpdateVisitRequest req, CancellationToken ct)
    {
        await updateValidator.ValidateOrThrowAsync(req, ct);
        if (req.Clinical is not null) EnsureCanWriteClinical();

        var visit = await (await VisibleAsync(db.Visits, ct)).FirstOrDefaultAsync(v => v.Id == id, ct)
                    ?? throw new NotFoundException(ErrorCodes.VisitNotFound);
        await EnsureTreatingDoctorAsync(visit.DoctorId, ct);
        // A completed visit is final: neither clinical content nor administrative fields change any more (T-013b).
        if (!visit.CanEditClinicalContent) throw new DomainException(ErrorCodes.VisitCompleted);
        db.SetOriginalRowVersion(visit, RowVersions.Decode(req.RowVersion));

        if (req.VisitedAtUtc is { } at) visit.VisitedAtUtc = ToUtc(at);
        visit.FollowUpDate = req.FollowUpDate;
        visit.FollowUpNotes = Clean(req.FollowUpNotes);
        if (req.Clinical is { } clinical) ApplyClinical(visit, clinical);

        // Section names only — never the clinical values.
        audit.Add(AuditActions.VisitUpdated, nameof(Visit), id.ToString(), details: new { clinical = req.Clinical is not null });
        await db.SaveChangesAsync(ct);
        return await LoadDtoAsync(id, ct);
    }

    /// <summary>
    /// Completes a visit (T-013b): Open → Completed, which locks it, and — if it was recorded for an appointment —
    /// moves that appointment CheckedIn → Completed under the normal appointment rules (already Completed: left as
    /// is; any other status: 422 INVALID_STATUS_TRANSITION). All or nothing: one transaction, serialized per visit
    /// with an app lock, optimistic concurrency on the visit's row version (stale → 409).
    /// Authorization: the endpoint needs Visits.Edit + MedicalRecords.Edit; the visit must be visible to the caller
    /// (tenant, branch and own-doctor rules — otherwise 404) and the caller must be its doctor (GD-01, else 403). Closing the appointment is part of closing the clinical
    /// encounter, so it does not additionally need Appointments.CheckIn (see docs/04-security-model.md).
    /// </summary>
    public async Task<VisitDto> CompleteAsync(Guid id, CompleteVisitRequest req, CancellationToken ct)
    {
        await completeValidator.ValidateOrThrowAsync(req, ct);
        var rowVersion = RowVersions.Decode(req.RowVersion);

        await db.InTransactionAsync(async c =>
        {
            await db.AcquireLockAsync($"visit-complete:{id:N}", c);
            var visit = await (await VisibleAsync(db.Visits, c)).FirstOrDefaultAsync(v => v.Id == id, c)
                        ?? throw new NotFoundException(ErrorCodes.VisitNotFound);
            await EnsureTreatingDoctorAsync(visit.DoctorId, c);
            db.SetOriginalRowVersion(visit, rowVersion);

            var now = clock.UtcNow;
            visit.Complete(now, user.UserId); // Open only, else 422 INVALID_STATUS_TRANSITION

            if (visit.AppointmentId is { } appointmentId
                && await db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, c) is { } appointment)
            {
                var from = appointment.Status;
                appointment.TransitionTo(AppointmentStatus.Completed, now); // no-op if already Completed
                if (from != AppointmentStatus.Completed)
                    audit.Add(AuditActions.AppointmentStatusChanged, nameof(Appointment), appointmentId.ToString(),
                        details: new { from = from.ToString(), to = AppointmentStatus.Completed.ToString(), via = "visit_completed" });
            }

            // Who and when only — never clinical content.
            audit.Add(AuditActions.VisitCompleted, nameof(Visit), id.ToString(), details: new { appointment = visit.AppointmentId is not null });
            await db.SaveChangesAsync(c);
            return true;
        }, ct: ct);

        return await LoadDtoAsync(id, ct);
    }

    // ------------------------------------------------------------------ helpers

    private void EnsureCanWriteClinical()
    {
        if (!user.HasPermission(Permissions.MedicalRecords.Edit)) throw new ForbiddenException();
    }

    /// <summary>GD-01: visits are written only by their own doctor (the caller's seat holds that doctor profile).</summary>
    private async Task EnsureTreatingDoctorAsync(Guid doctorId, CancellationToken ct)
    {
        if (await clinic.CallerDoctorIdAsync(ct) != doctorId) throw new ForbiddenException(ErrorCodes.VisitNotTreatingDoctor);
    }

    private async Task<VisitDto> LoadDtoAsync(Guid id, CancellationToken ct)
    {
        var v = await (await VisibleAsync(db.Visits.AsNoTracking(), ct)).FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException(ErrorCodes.VisitNotFound);
        var patient = await db.Patients.AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(p => p.Id == v.PatientId).Select(p => new { p.FullName, p.MedicalRecordNumber }).FirstAsync(ct);
        var doctorName = await db.Doctors.AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(d => d.Id == v.DoctorId).Select(d => d.DisplayNameAr).FirstAsync(ct);

        var clinical = CanReadClinical
            ? new VisitClinicalData(v.Symptoms, v.Diagnosis, v.DiagnosisCode, v.ClinicalNotes, v.TreatmentPlan,
                new VisitVitals(v.SystolicBp, v.DiastolicBp, v.HeartRate, v.RespiratoryRate, v.TemperatureC, v.OxygenSaturation, v.WeightKg, v.HeightCm))
            : null;
        return new VisitDto(v.Id, v.BranchId, v.PatientId, patient.FullName, patient.MedicalRecordNumber, v.DoctorId, doctorName,
            v.AppointmentId, v.VisitedAtUtc, v.FollowUpDate, v.FollowUpNotes, clinical, v.CreatedAtUtc, v.UpdatedAtUtc,
            Convert.ToBase64String(v.RowVersion), v.Status, v.CompletedAtUtc, v.CompletedByUserId);
    }

    /// <summary>Names via correlated subqueries so the page's sort order is kept. The diagnosis is masked in SQL.</summary>
    private Expression<Func<Visit, VisitListItem>> ListProjection(bool includeDiagnosis) => v => new VisitListItem(
        v.Id, v.BranchId, v.PatientId,
        db.Patients.Where(p => p.Id == v.PatientId).Select(p => p.FullName).FirstOrDefault() ?? "",
        db.Patients.Where(p => p.Id == v.PatientId).Select(p => p.MedicalRecordNumber).FirstOrDefault() ?? "",
        v.DoctorId,
        db.Doctors.Where(d => d.Id == v.DoctorId).Select(d => d.DisplayNameAr).FirstOrDefault() ?? "",
        v.AppointmentId, v.VisitedAtUtc, v.FollowUpDate,
        includeDiagnosis ? v.Diagnosis : null,
        v.Status, v.CompletedAtUtc);

    private async Task<Guid> ResolveBranchAsync(Guid? requested, CancellationToken ct)
    {
        if (requested is { } id)
        {
            if (!await db.Branches.AnyAsync(b => b.Id == id && b.IsActive, ct)) throw new NotFoundException(ErrorCodes.BranchNotFound);
            return id;
        }
        // Unambiguous defaults only: the member's single allowed branch, else the clinic's single active branch.
        if (clinic.RestrictedBranchIds.Count == 1) return clinic.RestrictedBranchIds[0];
        var candidates = await db.Branches.Where(b => b.IsActive).Select(b => b.Id).Take(2).ToListAsync(ct);
        if (candidates.Count != 1)
            throw new RequestValidationException(new Dictionary<string, string[]> { ["branchId"] = [ValidationCodes.Required] });
        return candidates[0];
    }

    /// <summary>The unique index UX_Visits_Appointment is the backstop when two visits race for one appointment.</summary>
    private async Task SaveWithAppointmentGuardAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UX_Visits_Appointment") == true)
        {
            throw new ConflictException(ErrorCodes.VisitAlreadyRecorded);
        }
    }

    private static void ApplyClinical(Visit v, VisitClinicalData c)
    {
        v.Symptoms = Clean(c.Symptoms);
        v.Diagnosis = Clean(c.Diagnosis);
        v.DiagnosisCode = Clean(c.DiagnosisCode)?.ToUpperInvariant();
        v.ClinicalNotes = Clean(c.ClinicalNotes);
        v.TreatmentPlan = Clean(c.TreatmentPlan);
        var vitals = c.Vitals;
        v.SystolicBp = vitals?.SystolicBp;
        v.DiastolicBp = vitals?.DiastolicBp;
        v.HeartRate = vitals?.HeartRate;
        v.RespiratoryRate = vitals?.RespiratoryRate;
        v.TemperatureC = vitals?.TemperatureC;
        v.OxygenSaturation = vitals?.OxygenSaturation;
        v.WeightKg = vitals?.WeightKg;
        v.HeightCm = vitals?.HeightCm;
    }

    /// <summary>
    /// Every visit read or edit goes through this: branch restriction, plus (T-012) a doctor without
    /// MedicalRecords.ViewAllDoctors sees only their own visits. Anything else is "not found" (404, no existence leak).
    /// </summary>
    private async Task<IQueryable<Visit>> VisibleAsync(IQueryable<Visit> q, CancellationToken ct)
    {
        q = RestrictToAllowedBranches(q);
        return await clinic.OwnDoctorScopeAsync(ct) is { } ownDoctor ? q.Where(v => v.DoctorId == ownDoctor) : q;
    }

    /// <summary>Branch-restricted members see only their branches; other branches' rows are "not found".</summary>
    private IQueryable<Visit> RestrictToAllowedBranches(IQueryable<Visit> q)
    {
        var allowed = clinic.RestrictedBranchIds;
        return allowed.Count == 0 ? q : q.Where(v => allowed.Contains(v.BranchId));
    }

    private IQueryable<Appointment> RestrictToAllowedBranches(IQueryable<Appointment> q)
    {
        var allowed = clinic.RestrictedBranchIds;
        return allowed.Count == 0 ? q : q.Where(a => allowed.Contains(a.BranchId));
    }

    private static DateTime ToUtc(DateTime value) =>
        DateTime.SpecifyKind(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value, DateTimeKind.Utc);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
