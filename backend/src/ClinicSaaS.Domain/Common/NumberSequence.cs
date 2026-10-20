namespace ClinicSaaS.Domain.Common;

/// <summary>
/// Per-tenant gap-free counter (MRN, invoice numbers…). Incremented with an atomic UPDATE … OUTPUT inside the
/// caller's transaction, so concurrent requests never get the same number and a rollback leaves no gap.
/// </summary>
public sealed class NumberSequence : ITenantOwned
{
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public long NextValue { get; set; } = 1;
}

public static class SequenceNames
{
    public const string PatientMrn = "patient_mrn";
    public const string Invoice = "invoice";
    public const string Prescription = "prescription";

    public static readonly IReadOnlyList<string> All = [PatientMrn, Invoice, Prescription];
}
