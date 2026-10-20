namespace ClinicSaaS.Domain.Common;

/// <summary>A business rule was violated. <see cref="Code"/> is stable and mapped to a localized message.</summary>
public class DomainException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}
