using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinicSaaS.Application.Attachments;

/// <summary>Attachment metadata. Never exposes the storage key, hash or any filesystem location.</summary>
public sealed record AttachmentDto(
    Guid Id, Guid PatientId, Guid? VisitId, string FileName, string ContentType, long SizeBytes, string Category,
    string? Description, string ScanStatus, bool CanDownload, DateTime CreatedAtUtc, Guid? CreatedByUserId);

public sealed record AttachmentListQuery : PageQuery
{
    public Guid? VisitId { get; init; }
}

/// <summary>One uploaded file as received by the API (the stream is read once, bounded by <see cref="AttachmentService.MaxFileBytes"/>).</summary>
public sealed record AttachmentUpload(
    string? FileName, string? ContentType, long Length, Stream? Content, string? Category, string? Description, Guid? VisitId);

/// <summary>An authorized, downloadable file: clean scan, visible to the caller. The caller disposes <see cref="Content"/>.</summary>
public sealed record AttachmentDownload(string FileName, string ContentType, long SizeBytes, Stream Content);

/// <summary>
/// Patient documents (P1-02). Rules:
/// - Files.Upload gates upload; every read (list, metadata, download) needs Files.View AND MedicalRecords.View
///   (controller). The patient and the optional visit must exist in the current clinic (tenant filter ⇒ "not found");
///   the visit must belong to the same patient.
/// - Visibility follows the record the file is attached to: a file linked to a visit is visible only to members who
///   may see that visit (branch restriction and, for a doctor without MedicalRecords.ViewAllDoctors, own visits only).
///   A file without a visit is clinic-wide like the patient, except for such own-records doctors: they see an unlinked
///   file only if they uploaded it (CreatedByUserId). Invisible files are "not found", never 403.
/// - Category is informational only (chosen by the uploader, never verified) and plays no part in authorization.
/// - Accepted types: PDF, JPEG, PNG, up to 10 MB. The type is decided from the file signature, and the extension and
///   declared MIME type must agree with it. The stored content type is the detected one.
/// - Storage key is random and server-generated; the client's file name is sanitized and used only for display and
///   the download name. Bytes are stored first, then metadata; if the metadata cannot be saved the bytes are removed.
/// - Malware scanning is not implemented: every file stays Pending and <see cref="FileAttachment.CanDownload"/> (Clean
///   only) keeps it quarantined. Nothing here marks a file Clean.
/// </summary>
public sealed class AttachmentService(
    IAppDbContext db,
    ClinicContext clinic,
    ICurrentUser user,
    IClock clock,
    IFileStorage storage,
    IAuditLogger audit,
    ILogger<AttachmentService> logger)
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFileNameLength = 255;
    private const int MaxDescriptionLength = 500;

    private sealed record AllowedType(string ContentType, string[] Extensions, Func<ReadOnlyMemory<byte>, bool> Matches);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray();
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    private static readonly AllowedType[] AllowedTypes =
    [
        new("application/pdf", [".pdf"], b => b.Span.StartsWith(PdfSignature)),
        new("image/jpeg", [".jpg", ".jpeg"], b => b.Span.StartsWith(JpegSignature)),
        new("image/png", [".png"], b => b.Span.StartsWith(PngSignature)),
    ];

    private static readonly Dictionary<string, Expression<Func<FileAttachment, object>>> Sorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["createdAt"] = a => a.CreatedAtUtc,
        ["fileName"] = a => a.FileName,
        ["size"] = a => a.SizeBytes,
    };

    public async Task<AttachmentDto> UploadAsync(Guid patientId, AttachmentUpload upload, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (upload.Content is null) errors["file"] = [ValidationCodes.Required];
        else if (upload.Length <= 0) errors["file"] = [ValidationCodes.FileEmpty];
        else if (upload.Length > MaxFileBytes) errors["file"] = [ValidationCodes.FileTooLarge];
        FileCategory category = FileCategory.Other;
        if (!string.IsNullOrWhiteSpace(upload.Category)
            && (!Enum.TryParse(upload.Category.Trim(), ignoreCase: true, out category) || !Enum.IsDefined(category) || int.TryParse(upload.Category, out _)))
            errors["category"] = [ValidationCodes.InvalidValue];
        var description = string.IsNullOrWhiteSpace(upload.Description) ? null : upload.Description.Trim();
        if (description is { Length: > MaxDescriptionLength }) errors["description"] = [ValidationCodes.TooLong];
        if (errors.Count > 0) throw new RequestValidationException(errors);

        if (!await db.Patients.AnyAsync(p => p.Id == patientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);
        if (upload.VisitId is { } visitId)
        {
            var visitPatient = await (await VisibleVisitsAsync(ct)).Where(v => v.Id == visitId).Select(v => (Guid?)v.PatientId).FirstOrDefaultAsync(ct)
                               ?? throw new NotFoundException(ErrorCodes.VisitNotFound);
            if (visitPatient != patientId)
                throw new RequestValidationException(new Dictionary<string, string[]> { ["visitId"] = [ValidationCodes.InvalidValue] });
        }

        // Bounded read: the declared length is not trusted.
        var bytes = await ReadBoundedAsync(upload.Content!, ct);
        if (bytes.Length == 0) throw FileError(ValidationCodes.FileEmpty);
        if (bytes.Length > MaxFileBytes) throw FileError(ValidationCodes.FileTooLarge);

        var fileName = SanitizeFileName(upload.FileName);
        var type = DetectType(bytes, fileName, upload.ContentType) ?? throw FileError(ValidationCodes.FileTypeNotAllowed);

        var now = clock.UtcNow;
        var key = $"{clinic.TenantId:N}/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}";
        var entity = new FileAttachment
        {
            PatientId = patientId,
            VisitId = upload.VisitId,
            FileName = fileName,
            ContentType = type.ContentType,
            SizeBytes = bytes.Length,
            StorageKey = key,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Category = category,
            Description = description,
        };

        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            await storage.SaveAsync(key, content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Attachment storage write failed");
            throw new AppException(ErrorCodes.FileStorageUnavailable, 503);
        }

        try
        {
            db.FileAttachments.Add(entity);
            // No file name or description in the audit trail: they may carry patient information.
            object details = upload.VisitId is { } linkedVisit
                ? new { patientId, visitId = linkedVisit, category = category.ToString(), contentType = type.ContentType, size = bytes.Length }
                : new { patientId, category = category.ToString(), contentType = type.ContentType, size = bytes.Length };
            audit.Add(AuditActions.FileUploaded, nameof(FileAttachment), entity.Id.ToString(), details: details);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Metadata not recorded ⇒ the bytes must not stay behind as an orphan. CancellationToken.None: the
            // cleanup must run even when the request was aborted.
            try { await storage.DeleteAsync(key, CancellationToken.None); }
            catch (Exception cleanup) { logger.LogError(cleanup, "Orphaned attachment bytes could not be removed after a failed save"); }
            throw;
        }

        return ToDto(entity);
    }

    public async Task<PagedResult<AttachmentDto>> ListForPatientAsync(Guid patientId, AttachmentListQuery query, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == patientId, ct)) throw new NotFoundException(ErrorCodes.PatientNotFound);
        var q = (await VisibleAsync(db.FileAttachments.AsNoTracking(), ct)).Where(a => a.PatientId == patientId);
        if (query.VisitId is { } visitId) q = q.Where(a => a.VisitId == visitId);
        var page = await q.ApplySort(query.Sort, Sorts, "-createdAt").ToPagedResultAsync(query, a => new AttachmentDto(
            a.Id, a.PatientId, a.VisitId, a.FileName, a.ContentType, a.SizeBytes, a.Category.ToString(), a.Description,
            a.ScanStatus.ToString(), a.ScanStatus == FileScanStatus.Clean, a.CreatedAtUtc, a.CreatedByUserId), ct);

        // Who looked at which patient's documents — ids and counts only, never names or descriptions.
        audit.Add(AuditActions.FilesListed, "Patient", patientId.ToString(),
            details: new { visitId = query.VisitId, page = page.Page, returned = page.Items.Count, total = page.TotalCount });
        await db.SaveChangesAsync(ct);
        return page;
    }

    public async Task<AttachmentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var a = await LoadVisibleAsync(id, ct);
        audit.Add(AuditActions.FileViewed, nameof(FileAttachment), a.Id.ToString(),
            details: a.VisitId is { } visitId ? new { patientId = a.PatientId, visitId } : new { patientId = a.PatientId });
        await db.SaveChangesAsync(ct);
        return ToDto(a);
    }

    /// <summary>Returns the bytes of a visible, clean file. Quarantined files (anything but Clean) are refused.</summary>
    public async Task<AttachmentDownload> OpenDownloadAsync(Guid id, CancellationToken ct)
    {
        var a = await LoadVisibleAsync(id, ct);
        if (!a.CanDownload)
            throw new AppException(ErrorCodes.FileNotAvailable, 422, data: new { scanStatus = a.ScanStatus.ToString() });

        Stream? content;
        try
        {
            content = await storage.OpenReadAsync(a.StorageKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Attachment storage read failed for {AttachmentId}", a.Id);
            throw new AppException(ErrorCodes.FileStorageUnavailable, 503);
        }
        if (content is null)
        {
            logger.LogError("Attachment {AttachmentId} has metadata but no stored bytes", a.Id);
            throw new AppException(ErrorCodes.FileStorageUnavailable, 503);
        }

        try
        {
            audit.Add(AuditActions.FileDownloaded, nameof(FileAttachment), a.Id.ToString(), details: new { patientId = a.PatientId });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
        return new AttachmentDownload(a.FileName, a.ContentType, a.SizeBytes, content);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<FileAttachment> LoadVisibleAsync(Guid id, CancellationToken ct) =>
        await (await VisibleAsync(db.FileAttachments.AsNoTracking(), ct)).FirstOrDefaultAsync(a => a.Id == id, ct)
        ?? throw new NotFoundException(ErrorCodes.FileNotFound);

    /// <summary>
    /// Linked files: same scope as their visit (branch restriction + T-012 own-doctor rule); the visit lookup lifts only
    /// the soft-delete filter, so a file keeps its visit's scope even if the visit is ever soft-deleted. Unlinked files:
    /// clinic-wide, except that an own-records doctor sees only the ones they uploaded. The single rule behind list,
    /// metadata and download.
    /// </summary>
    private async Task<IQueryable<FileAttachment>> VisibleAsync(IQueryable<FileAttachment> q, CancellationToken ct)
    {
        var allowed = clinic.RestrictedBranchIds;
        var ownDoctor = await clinic.OwnDoctorScopeAsync(ct);
        if (allowed.Count == 0 && ownDoctor is null) return q;
        // No user id ⇒ matches nothing (never "uploaded by null").
        var uploader = user.UserId ?? Guid.Empty;
        var visits = db.Visits.IgnoreQueryFilters([QueryFilters.SoftDelete])
            .Where(v => allowed.Count == 0 || allowed.Contains(v.BranchId))
            .Where(v => ownDoctor == null || v.DoctorId == ownDoctor);
        return q.Where(a => a.VisitId == null
            ? ownDoctor == null || a.CreatedByUserId == uploader
            : visits.Any(v => v.Id == a.VisitId));
    }

    private async Task<IQueryable<Visit>> VisibleVisitsAsync(CancellationToken ct)
    {
        var allowed = clinic.RestrictedBranchIds;
        var ownDoctor = await clinic.OwnDoctorScopeAsync(ct);
        return db.Visits.AsNoTracking()
            .Where(v => allowed.Count == 0 || allowed.Contains(v.BranchId))
            .Where(v => ownDoctor == null || v.DoctorId == ownDoctor);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxFileBytes) break; // reported as too large by the caller
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The signature decides the type; the (sanitized) extension and the declared MIME type must both agree with it.
    /// </summary>
    private static AllowedType? DetectType(byte[] bytes, string fileName, string? declaredContentType)
    {
        var detected = AllowedTypes.FirstOrDefault(t => t.Matches(bytes));
        if (detected is null) return null;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!detected.Extensions.Contains(extension)) return null;
        var declared = (declaredContentType ?? "").Split(';')[0].Trim();
        return string.Equals(declared, detected.ContentType, StringComparison.OrdinalIgnoreCase) ? detected : null;
    }

    /// <summary>
    /// Display name only: last path segment, no control/format characters (incl. bidi overrides that can disguise an
    /// extension), no characters reserved by common filesystems, bounded length with the extension kept.
    /// </summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var lastSegment = name.Replace('\\', '/').Split('/').Last();
        var sb = new StringBuilder(lastSegment.Length);
        foreach (var ch in lastSegment.Normalize(NormalizationForm.FormC))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) continue;
            sb.Append("<>:\"|?*".Contains(ch) ? '_' : ch);
        }
        var cleaned = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('.', ' ');
        if (cleaned.Length <= MaxFileNameLength) return cleaned;
        var extension = Path.GetExtension(cleaned);
        if (extension.Length > 10) extension = "";
        return cleaned[..(MaxFileNameLength - extension.Length)].TrimEnd() + extension;
    }

    private static RequestValidationException FileError(string code) =>
        new(new Dictionary<string, string[]> { ["file"] = [code] });

    private static AttachmentDto ToDto(FileAttachment a) => new(
        a.Id, a.PatientId, a.VisitId, a.FileName, a.ContentType, a.SizeBytes, a.Category.ToString(), a.Description,
        a.ScanStatus.ToString(), a.CanDownload, a.CreatedAtUtc, a.CreatedByUserId);
}
