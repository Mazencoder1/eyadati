using System.Data;
using System.Linq.Expressions;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Patients;

/// <summary>
/// Patient use cases. Tenant isolation is enforced by the data layer (every query below is already scoped to
/// the caller's clinic); this service adds permission-level rules and business rules on top.
/// </summary>
public sealed class PatientService(
    IAppDbContext db,
    ICurrentUser user,
    ITenantContext tenant,
    ISequenceGenerator sequences,
    IUsageLimiter limits,
    IAuditLogger audit,
    IValidator<SavePatientRequest> createValidator,
    IValidator<UpdatePatientRequest> updateValidator,
    IValidator<UpdatePatientMedicalRequest> medicalValidator)
{
    private static readonly Dictionary<string, Expression<Func<Patient, object>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["createdAt"] = p => p.CreatedAtUtc,
        ["fullName"] = p => p.FullName,
        ["mrn"] = p => p.MedicalRecordNumber,
    };

    public Task<PagedResult<PatientListItem>> ListAsync(PatientListQuery query, CancellationToken ct)
    {
        var q = db.Patients.AsNoTracking();
        if (query.Gender is { } g) q = q.Where(p => p.Gender == g);
        q = ApplySearch(q, query.Search);

        return q.ApplySort(query.Sort, Sorts, "-createdAt").ToPagedResultAsync(query,
            p => new PatientListItem(p.Id, p.MedicalRecordNumber, p.FullName, p.MobileNumber, p.Gender, p.DateOfBirth, p.CreatedAtUtc), ct);
    }

    public async Task<PatientDetails> GetAsync(Guid id, CancellationToken ct)
    {
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct)
                      ?? throw new NotFoundException(ErrorCodes.PatientNotFound);
        audit.Add(AuditActions.PatientViewed, nameof(Patient), patient.Id.ToString(),
            details: user.HasPermission(Permissions.MedicalRecords.View) ? new { medical = true } : null);
        await db.SaveChangesAsync(ct);
        return ToDetails(patient);
    }

    /// <summary>Same mobile or same identity number — shown as a warning (families often share one mobile).</summary>
    public async Task<IReadOnlyList<DuplicateCandidate>> FindDuplicatesAsync(string? mobile, string? identityNumber, CancellationToken ct)
    {
        var normalized = SaudiFormats.NormalizeMobile(mobile);
        var id = string.IsNullOrWhiteSpace(identityNumber) ? null : SaudiFormats.ToLatinDigits(identityNumber.Trim());
        if (normalized is null && id is null) return [];

        return await db.Patients.AsNoTracking()
            .Where(p => (normalized != null && p.MobileNumber == normalized) || (id != null && p.IdentityNumber == id))
            .OrderBy(p => p.FullName)
            .Take(10)
            .Select(p => new DuplicateCandidate(p.Id, p.MedicalRecordNumber, p.FullName, p.MobileNumber))
            .ToListAsync(ct);
    }

    public async Task<PatientDetails> CreateAsync(SavePatientRequest req, CancellationToken ct)
    {
        await createValidator.ValidateOrThrowAsync(req, ct);
        await limits.EnsureCanAddAsync(LimitKeys.MaxPatients, ct);
        var tenantId = tenant.TenantId ?? throw new ForbiddenException(ErrorCodes.TenantRequired);

        var patient = await db.InTransactionAsync(async c =>
        {
            await EnsureUniqueIdentityAsync(req, excludeId: null, c);
            await EnsureBranchAsync(req.RegisteredBranchId, c);

            var mrn = await sequences.NextAsync(tenantId, SequenceNames.PatientMrn, c);
            var entity = new Patient
            {
                MedicalRecordNumber = Patient.FormatMrn(mrn),
                FullName = "",
                MobileNumber = "",
            };
            Apply(entity, req);
            db.Patients.Add(entity);
            audit.Add(AuditActions.PatientCreated, nameof(Patient), entity.Id.ToString());
            await db.SaveChangesAsync(c);
            return entity;
        }, IsolationLevel.ReadCommitted, ct);

        return ToDetails(patient);
    }

    public async Task<PatientDetails> UpdateAsync(Guid id, UpdatePatientRequest req, CancellationToken ct)
    {
        await updateValidator.ValidateOrThrowAsync(req, ct);
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, ct)
                      ?? throw new NotFoundException(ErrorCodes.PatientNotFound);
        db.SetOriginalRowVersion(patient, DecodeRowVersion(req.RowVersion));

        await EnsureUniqueIdentityAsync(req, excludeId: id, ct);
        await EnsureBranchAsync(req.RegisteredBranchId, ct);
        Apply(patient, req);
        audit.Add(AuditActions.PatientUpdated, nameof(Patient), id.ToString());
        await db.SaveChangesAsync(ct);
        return ToDetails(patient);
    }

    public async Task<PatientDetails> UpdateMedicalAsync(Guid id, UpdatePatientMedicalRequest req, CancellationToken ct)
    {
        await medicalValidator.ValidateOrThrowAsync(req, ct);
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, ct)
                      ?? throw new NotFoundException(ErrorCodes.PatientNotFound);
        db.SetOriginalRowVersion(patient, DecodeRowVersion(req.RowVersion));

        patient.BloodGroup = req.BloodGroup;
        patient.Allergies = Clean(req.Allergies);
        patient.ChronicConditions = Clean(req.ChronicConditions);
        patient.MedicalNotes = Clean(req.MedicalNotes);
        // Field names only — never the clinical values.
        audit.Add(AuditActions.PatientUpdated, nameof(Patient), id.ToString(), details: new { section = "medical" });
        await db.SaveChangesAsync(ct);
        return ToDetails(patient);
    }

    /// <summary>Soft delete: medical history is never physically removed by a clinic user.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == id, ct)
                      ?? throw new NotFoundException(ErrorCodes.PatientNotFound);
        // D6: deletion is only for registration mistakes. Any appointment (whatever its status), visit, prescription
        // or file — soft-deleted ones included — is history that must stay attached to a visible patient.
        if (await db.Appointments.AnyAsync(a => a.PatientId == id, ct)
            || await db.Visits.IgnoreQueryFilters([QueryFilters.SoftDelete]).AnyAsync(v => v.PatientId == id, ct)
            || await db.Prescriptions.IgnoreQueryFilters([QueryFilters.SoftDelete]).AnyAsync(r => r.PatientId == id, ct)
            || await db.FileAttachments.IgnoreQueryFilters([QueryFilters.SoftDelete]).AnyAsync(f => f.PatientId == id, ct))
            throw new ConflictException(ErrorCodes.PatientHasHistory);
        db.Patients.Remove(patient);
        audit.Add(AuditActions.PatientDeleted, nameof(Patient), id.ToString());
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ helpers

    private static IQueryable<Patient> ApplySearch(IQueryable<Patient> q, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return q;
        var term = SaudiFormats.ToLatinDigits(search.Trim());
        if (term.Length > 100) term = term[..100];

        // Each branch hits an index: (TenantId, MobileNumber), (TenantId, MedicalRecordNumber),
        // (TenantId, IdentityNumber), (TenantId, FullName) prefix. Name "contains" search moves to full-text in phase 12.
        if (SaudiFormats.NormalizeMobile(term) is { } mobile) return q.Where(p => p.MobileNumber == mobile);
        if (term.StartsWith("P-", StringComparison.OrdinalIgnoreCase)) return q.Where(p => p.MedicalRecordNumber.StartsWith(term.ToUpper()));
        if (term.Length == 10 && term.All(char.IsAsciiDigit)) return q.Where(p => p.IdentityNumber == term);
        return q.Where(p => p.FullName.StartsWith(term) || (p.FullNameEn != null && p.FullNameEn.StartsWith(term)));
    }

    private async Task EnsureUniqueIdentityAsync(SavePatientRequest req, Guid? excludeId, CancellationToken ct)
    {
        if (req.IdentityType == IdentityDocumentType.None || string.IsNullOrWhiteSpace(req.IdentityNumber)) return;
        var number = SaudiFormats.ToLatinDigits(req.IdentityNumber.Trim());
        var exists = await db.Patients.AnyAsync(p =>
            p.IdentityType == req.IdentityType && p.IdentityNumber == number && (excludeId == null || p.Id != excludeId), ct);
        if (exists) throw new ConflictException(ErrorCodes.DuplicatePatientIdentity);
    }

    private async Task EnsureBranchAsync(Guid? branchId, CancellationToken ct)
    {
        // The branch must exist in *this* tenant — the tenant filter makes a foreign id simply "not found".
        if (branchId is { } id && !await db.Branches.AnyAsync(b => b.Id == id, ct))
            throw new RequestValidationException(new Dictionary<string, string[]> { ["registeredBranchId"] = [ValidationCodes.InvalidValue] });
    }

    private static void Apply(Patient p, SavePatientRequest req)
    {
        p.FullName = req.FullName.Trim();
        p.FullNameEn = Clean(req.FullNameEn);
        p.MobileNumber = SaudiFormats.NormalizeMobile(req.MobileNumber)!;
        p.Email = Clean(req.Email)?.ToLowerInvariant();
        p.IdentityType = req.IdentityType;
        p.IdentityNumber = req.IdentityType == IdentityDocumentType.None ? null : SaudiFormats.ToLatinDigits(req.IdentityNumber!.Trim());
        p.DateOfBirth = req.DateOfBirth;
        p.Gender = req.Gender;
        p.Nationality = Clean(req.Nationality)?.ToUpperInvariant();
        p.Notes = Clean(req.Notes);
        p.RegisteredBranchId = req.RegisteredBranchId;
    }

    private PatientDetails ToDetails(Patient p)
    {
        var medical = user.HasPermission(Permissions.MedicalRecords.View)
            ? new PatientMedicalInfo(p.BloodGroup, p.Allergies, p.ChronicConditions, p.MedicalNotes)
            : null;
        var identity = user.HasPermission(Permissions.Patients.Edit) ? p.IdentityNumber : Mask(p.IdentityNumber);
        return new PatientDetails(p.Id, p.MedicalRecordNumber, p.FullName, p.FullNameEn, p.MobileNumber, p.Email,
            p.IdentityType, identity, p.DateOfBirth, p.Gender, p.Nationality, p.Notes, medical,
            p.CreatedAtUtc, p.UpdatedAtUtc, Convert.ToBase64String(p.RowVersion));
    }

    private static string? Mask(string? value) =>
        value is null || value.Length <= 4 ? value : new string('•', value.Length - 4) + value[^4..];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static byte[] DecodeRowVersion(string value)
    {
        try { return Convert.FromBase64String(value); }
        catch (FormatException)
        {
            throw new RequestValidationException(new Dictionary<string, string[]> { ["rowVersion"] = [ValidationCodes.InvalidValue] });
        }
    }
}
