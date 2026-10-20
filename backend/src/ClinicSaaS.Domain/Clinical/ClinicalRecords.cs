using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Clinical;

/// <summary>
/// Lifecycle of a visit (migration 004). Open → Completed only; Completed is final and locks the clinical content.
/// Stored as nvarchar(32) and guarded by <c>CK_Visits_Status</c>.
/// </summary>
public enum VisitStatus { Open, Completed }

/// <summary>
/// A clinical encounter: what the doctor found and did. Usually recorded for a checked-in appointment (which it
/// then completes), but walk-in encounters without an appointment are allowed. Never physically deleted.
/// </summary>
public sealed class Visit : TenantEntity, ISoftDeletable, IConcurrencyAware
{
    public Guid BranchId { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid? AppointmentId { get; set; }
    public DateTime VisitedAtUtc { get; set; }

    // --- clinical content: readable only with MedicalRecords.View, never written to logs or audit details ---
    /// <summary>Presenting complaint / symptoms.</summary>
    public string? Symptoms { get; set; }
    public string? Diagnosis { get; set; }
    /// <summary>Optional ICD-10 code (e.g. J06.9).</summary>
    public string? DiagnosisCode { get; set; }
    public string? ClinicalNotes { get; set; }
    public string? TreatmentPlan { get; set; }

    // --- vitals ---
    public int? SystolicBp { get; set; }
    public int? DiastolicBp { get; set; }
    public int? HeartRate { get; set; }
    public int? RespiratoryRate { get; set; }
    public decimal? TemperatureC { get; set; }
    public int? OxygenSaturation { get; set; }
    public decimal? WeightKg { get; set; }
    public decimal? HeightCm { get; set; }

    // --- follow-up (administrative: the front desk books it) ---
    public DateOnly? FollowUpDate { get; set; }
    public string? FollowUpNotes { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    // --- lifecycle (migration 004): changed only through Complete() ---
    /// <summary>Every visit starts Open; rows that existed before migration 004 were defaulted to Open.</summary>
    public VisitStatus Status { get; private set; } = VisitStatus.Open;
    public DateTime? CompletedAtUtc { get; private set; }
    /// <summary>Who completed the visit (audit column, no FK — same convention as <see cref="DeletedByUserId"/>).</summary>
    public Guid? CompletedByUserId { get; private set; }

    public bool IsCompleted => Status == VisitStatus.Completed;

    /// <summary>Clinical content (findings, vitals, follow-up) may be written only while the visit is Open and not deleted.</summary>
    public bool CanEditClinicalContent => Status == VisitStatus.Open && !IsDeleted;

    /// <summary>
    /// Closes the visit: Open → Completed, stamping when and by whom. Any other transition (including completing an
    /// already-completed visit) is refused with <see cref="ErrorCodes.InvalidStatusTransition"/>.
    /// </summary>
    public void Complete(DateTime nowUtc, Guid? userId)
    {
        if (Status != VisitStatus.Open)
            throw new DomainException(ErrorCodes.InvalidStatusTransition, $"{Status} -> {VisitStatus.Completed}");
        Status = VisitStatus.Completed;
        CompletedAtUtc = nowUtc;
        CompletedByUserId = userId;
    }

    /// <summary>Appointment statuses for which a visit may be recorded.</summary>
    public static readonly AppointmentStatus[] RecordableAppointmentStatuses = [AppointmentStatus.CheckedIn, AppointmentStatus.Completed];
}

/// <summary>
/// A prescription issued by a doctor. Treated as a legal document: issued once, never edited (a correction is a
/// new prescription). Numbered per clinic (RX-000001) from the gap-free tenant sequence.
/// </summary>
public sealed class Prescription : TenantEntity, ISoftDeletable, IConcurrencyAware
{
    public required string Number { get; set; }
    public Guid PatientId { get; set; }
    public Guid DoctorId { get; set; }
    public Guid? VisitId { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public string? Notes { get; set; }

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PrescriptionItem> Items { get; set; } = [];

    public const int MaxItems = 30;

    public static string FormatNumber(long sequence) => $"RX-{sequence:D6}";
}

public sealed class PrescriptionItem : TenantEntity
{
    public Guid PrescriptionId { get; set; }
    public int LineNumber { get; set; }
    public required string MedicationName { get; set; }
    /// <summary>e.g. "500 mg".</summary>
    public string? Strength { get; set; }
    /// <summary>e.g. "1 tablet".</summary>
    public string? Dosage { get; set; }
    /// <summary>e.g. "3 times daily".</summary>
    public string? Frequency { get; set; }
    /// <summary>e.g. "oral".</summary>
    public string? Route { get; set; }
    public int? DurationDays { get; set; }
    public string? Instructions { get; set; }
}

public enum FileCategory { LabResult, Imaging, Report, Prescription, Identity, Insurance, Other }

/// <summary>
/// Malware-scan lifecycle of an uploaded file. Only <see cref="Clean"/> files may ever be downloaded.
/// Pending → Scanning → Clean | Rejected | Failed; a Failed scan may be retried (→ Pending).
/// Clean and Rejected are final: a rejected file is never re-admitted, and a clean file is never re-opened.
/// </summary>
public enum FileScanStatus { Pending, Scanning, Clean, Rejected, Failed }

/// <summary>
/// Metadata of a patient document. The bytes live in private storage (<c>IFileStorage</c>) under a
/// server-generated key — never under a public path and never addressable by the client-supplied name.
/// </summary>
public sealed class FileAttachment : TenantEntity, ISoftDeletable
{
    public static readonly IReadOnlyDictionary<FileScanStatus, FileScanStatus[]> ScanTransitions =
        new Dictionary<FileScanStatus, FileScanStatus[]>
        {
            [FileScanStatus.Pending] = [FileScanStatus.Scanning, FileScanStatus.Failed],
            [FileScanStatus.Scanning] = [FileScanStatus.Clean, FileScanStatus.Rejected, FileScanStatus.Failed],
            [FileScanStatus.Failed] = [FileScanStatus.Pending],
            [FileScanStatus.Clean] = [],
            [FileScanStatus.Rejected] = [],
        };

    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    /// <summary>Sanitized original name, for display and the download's Content-Disposition only.</summary>
    public required string FileName { get; set; }
    /// <summary>Content type decided by the server from the verified file signature, not by the client.</summary>
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
    public required string StorageKey { get; set; }
    /// <summary>SHA-256 hex of the stored bytes (integrity check / dedup).</summary>
    public required string Sha256 { get; set; }
    public FileCategory Category { get; set; } = FileCategory.Other;
    public string? Description { get; set; }
    /// <summary>Every new file starts Pending and is not downloadable until a scanner marks it Clean.</summary>
    public FileScanStatus ScanStatus { get; private set; } = FileScanStatus.Pending;

    public bool IsDeleted { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public Guid? DeletedByUserId { get; set; }

    /// <summary>The only gate for serving the bytes: scanned clean and not deleted.</summary>
    public bool CanDownload => ScanStatus == FileScanStatus.Clean && !IsDeleted;

    public static bool CanTransition(FileScanStatus from, FileScanStatus to) =>
        ScanTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public void TransitionScanTo(FileScanStatus to)
    {
        if (to == ScanStatus) return;
        if (!CanTransition(ScanStatus, to)) throw new DomainException(ErrorCodes.InvalidStatusTransition, $"{ScanStatus} -> {to}");
        ScanStatus = to;
    }
}
