using System.Linq.Expressions;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Prescriptions;

public sealed record PrescriptionItemData(
    string MedicationName, string? Strength, string? Dosage, string? Frequency, string? Route, int? DurationDays, string? Instructions);

public sealed record PrescriptionItemDto(
    int LineNumber, string MedicationName, string? Strength, string? Dosage, string? Frequency, string? Route, int? DurationDays,
    string? Instructions);

/// <summary>An issued prescription. Immutable: there is no update endpoint and therefore no rowVersion.</summary>
public sealed record PrescriptionDto(
    Guid Id, string Number, Guid PatientId, string PatientName, string PatientMrn, Guid DoctorId, string DoctorName,
    Guid? VisitId, DateTime IssuedAtUtc, string? Notes, IReadOnlyList<PrescriptionItemDto> Items, DateTime CreatedAtUtc);

public sealed record PrescriptionListItem(
    Guid Id, string Number, Guid PatientId, Guid DoctorId, string DoctorName, Guid? VisitId, DateTime IssuedAtUtc, int ItemCount);

public sealed record PrescriptionListQuery : PageQuery;

/// <summary>
/// D8: a prescription is always issued from a visit. Patient, doctor and branch come from that visit and the
/// signed-in caller — never from the client — so the request carries only the visit and the content.
/// </summary>
public sealed record CreatePrescriptionRequest(Guid VisitId, string? Notes, IReadOnlyList<PrescriptionItemData>? Items);

public sealed class PrescriptionItemValidator : AbstractValidator<PrescriptionItemData>
{
    public PrescriptionItemValidator()
    {
        RuleFor(x => x.MedicationName).RequiredText().MaxLen(200);
        RuleFor(x => x.Strength).MaxLen(100);
        RuleFor(x => x.Dosage).MaxLen(100);
        RuleFor(x => x.Frequency).MaxLen(100);
        RuleFor(x => x.Route).MaxLen(50);
        // Same bounds as CK_PrescriptionItems_Duration.
        RuleFor(x => x.DurationDays).InclusiveBetween(1, 365).When(x => x.DurationDays is not null).WithMessage(ValidationCodes.OutOfRange);
        RuleFor(x => x.Instructions).MaxLen(500);
    }
}

public sealed class CreatePrescriptionValidator : AbstractValidator<CreatePrescriptionRequest>
{
    public CreatePrescriptionValidator()
    {
        RuleFor(x => x.VisitId).NotEmpty().WithMessage(ValidationCodes.Required);
        RuleFor(x => x.Notes).MaxLen(1000);
        RuleFor(x => x.Items).NotEmpty().WithMessage(ValidationCodes.Required)
            .Must(i => i is null || i.Count <= Prescription.MaxItems).WithMessage(ValidationCodes.OutOfRange);
        RuleForEach(x => x.Items).SetValidator(new PrescriptionItemValidator());
    }
}

/// <summary>
/// Prescriptions are legal documents: issued once, never edited (a correction is a new prescription), numbered
/// per clinic (RX-000001) from the gap-free tenant sequence. Rules:
/// - Issuing (D8): every new prescription belongs to a visit the caller can see (tenant filter, branch restriction,
///   own-doctor rule ⇒ otherwise "not found"). Its patient and doctor are the visit's, and the caller must be that
///   visit's doctor (their seat's doctor profile) — a non-doctor or another doctor gets 403, whatever their role.
/// - Branch isolation: a prescription linked to a visit belongs to that visit's branch, so branch-restricted members
///   neither see nor link prescriptions of other branches' visits. Older prescriptions without a visit stay
///   clinic-wide, like the patient they belong to.
/// - Prescriptions.View/Create gate the endpoints. No delete: no permission covers it.
/// </summary>
public sealed class PrescriptionService(
    IAppDbContext db,
    ClinicContext clinic,
    IClock clock,
    ISequenceGenerator sequences,
    IAuditLogger audit,
    IValidator<CreatePrescriptionRequest> createValidator)
{
    private static readonly Dictionary<string, Expression<Func<Prescription, object>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["issuedAt"] = p => p.IssuedAtUtc,
        ["number"] = p => p.Number,
    };

    public async Task<PagedResult<PrescriptionListItem>> ListForPatientAsync(Guid patientId, PrescriptionListQuery query, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);
        var q = await VisibleAsync(db.Prescriptions.AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(p => !p.IsDeleted && p.PatientId == patientId), ct);

        return await q.ApplySort(query.Sort, Sorts, "-issuedAt").ToPagedResultAsync(query, p => new PrescriptionListItem(
            p.Id, p.Number, p.PatientId, p.DoctorId,
            db.Doctors.Where(d => d.Id == p.DoctorId).Select(d => d.DisplayNameAr).FirstOrDefault() ?? "",
            p.VisitId, p.IssuedAtUtc, p.Items.Count), ct);
    }

    public async Task<PrescriptionDto> GetAsync(Guid id, CancellationToken ct)
    {
        var dto = await LoadDtoAsync(id, ct);
        audit.Add(AuditActions.PrescriptionViewed, nameof(Prescription), id.ToString());
        await db.SaveChangesAsync(ct);
        return dto;
    }

    public async Task<PrescriptionDto> CreateAsync(CreatePrescriptionRequest req, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(req, ct);
        var tenantId = clinic.TenantId;

        var allowed = clinic.RestrictedBranchIds;
        var ownDoctor = await clinic.OwnDoctorScopeAsync(ct);
        var visit = await db.Visits.AsNoTracking()
                        .Where(v => v.Id == req.VisitId && (allowed.Count == 0 || allowed.Contains(v.BranchId)))
                        .Where(v => ownDoctor == null || v.DoctorId == ownDoctor)
                        .Select(v => new { v.PatientId, v.DoctorId }).FirstOrDefaultAsync(ct)
                    ?? throw new NotFoundException(ErrorCodes.VisitNotFound);

        // The prescriber is whoever is signed in: their seat's doctor profile must be the visit's doctor.
        var callerDoctor = await clinic.CallerDoctorIdAsync(ct);
        if (callerDoctor is null || callerDoctor != visit.DoctorId) throw new ForbiddenException(ErrorCodes.PrescriberNotTreatingDoctor);

        var doctor = await db.Doctors.AsNoTracking().FirstOrDefaultAsync(d => d.Id == visit.DoctorId, ct)
                     ?? throw new NotFoundException(ErrorCodes.DoctorNotFound);
        if (!doctor.IsActive) throw new DomainException(ErrorCodes.DoctorInactive);
        if (!await db.Patients.AnyAsync(p => p.Id == visit.PatientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);

        var now = clock.UtcNow;
        var prescription = await db.InTransactionAsync(async c =>
        {
            // A completed visit is clinically locked: prescriptions are written while the visit is open. The check runs
            // under the same per-visit lock as completion, so a prescription and a completion can't interleave.
            await db.AcquireLockAsync($"visit-complete:{req.VisitId:N}", c);
            if (await db.Visits.AsNoTracking().AnyAsync(v => v.Id == req.VisitId && v.Status == VisitStatus.Completed, c))
                throw new DomainException(ErrorCodes.VisitCompleted);

            var number = await sequences.NextAsync(tenantId, SequenceNames.Prescription, c);
            var entity = new Prescription
            {
                Number = Prescription.FormatNumber(number),
                PatientId = visit.PatientId,
                DoctorId = doctor.Id,
                VisitId = req.VisitId,
                IssuedAtUtc = now,
                Notes = Clean(req.Notes),
                Items = req.Items!.Select((i, index) => new PrescriptionItem
                {
                    LineNumber = index + 1,
                    MedicationName = i.MedicationName.Trim(),
                    Strength = Clean(i.Strength),
                    Dosage = Clean(i.Dosage),
                    Frequency = Clean(i.Frequency),
                    Route = Clean(i.Route),
                    DurationDays = i.DurationDays,
                    Instructions = Clean(i.Instructions),
                }).ToList(),
            };
            db.Prescriptions.Add(entity);
            // Number and item count only — never the medications.
            audit.Add(AuditActions.PrescriptionIssued, nameof(Prescription), entity.Id.ToString(),
                details: new { number = entity.Number, items = entity.Items.Count, visitId = req.VisitId });
            await db.SaveChangesAsync(c);
            return entity;
        }, ct: ct);

        return await LoadDtoAsync(prescription.Id, ct);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<PrescriptionDto> LoadDtoAsync(Guid id, CancellationToken ct)
    {
        var p = await (await VisibleAsync(db.Prescriptions.AsNoTracking().Include(x => x.Items), ct))
                    .FirstOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new NotFoundException(ErrorCodes.PrescriptionNotFound);
        var patient = await db.Patients.AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(x => x.Id == p.PatientId).Select(x => new { x.FullName, x.MedicalRecordNumber }).FirstAsync(ct);
        var doctorName = await db.Doctors.AsNoTracking().IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(d => d.Id == p.DoctorId).Select(d => d.DisplayNameAr).FirstAsync(ct);

        var items = p.Items.OrderBy(i => i.LineNumber)
            .Select(i => new PrescriptionItemDto(i.LineNumber, i.MedicationName, i.Strength, i.Dosage, i.Frequency, i.Route, i.DurationDays, i.Instructions))
            .ToList();
        return new PrescriptionDto(p.Id, p.Number, p.PatientId, patient.FullName, patient.MedicalRecordNumber, p.DoctorId, doctorName,
            p.VisitId, p.IssuedAtUtc, p.Notes, items, p.CreatedAtUtc);
    }

    /// <summary>
    /// Branch restriction plus (T-012) the own-doctor rule: a doctor without MedicalRecords.ViewAllDoctors sees only
    /// prescriptions issued in their name. Anything else is "not found".
    /// </summary>
    private async Task<IQueryable<Prescription>> VisibleAsync(IQueryable<Prescription> q, CancellationToken ct)
    {
        q = RestrictToAllowedBranches(q);
        return await clinic.OwnDoctorScopeAsync(ct) is { } ownDoctor ? q.Where(p => p.DoctorId == ownDoctor) : q;
    }

    /// <summary>
    /// A prescription linked to a visit is visible only to members allowed on that visit's branch. The lookup lifts
    /// the soft-delete filter so a prescription keeps its branch even if its visit is ever soft-deleted.
    /// </summary>
    private IQueryable<Prescription> RestrictToAllowedBranches(IQueryable<Prescription> q)
    {
        var allowed = clinic.RestrictedBranchIds;
        if (allowed.Count == 0) return q;
        var visits = db.Visits.IgnoreQueryFilters([QueryFilters.SoftDelete]);
        return q.Where(p => p.VisitId == null || visits.Any(v => v.Id == p.VisitId && allowed.Contains(v.BranchId)));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
