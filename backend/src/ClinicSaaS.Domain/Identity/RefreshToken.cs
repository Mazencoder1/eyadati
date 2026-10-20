using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Identity;

/// <summary>
/// Rotating refresh token. Only a SHA-256 hash is stored. Every refresh issues a new token in the same
/// <see cref="FamilyId"/>; presenting an already-rotated token revokes the whole family (theft detection).
/// </summary>
public sealed class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    /// <summary>The clinic this session is bound to (null for platform-only sessions).</summary>
    public Guid? TenantId { get; set; }
    public required string TokenHash { get; set; }
    public Guid FamilyId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? CreatedByIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedReason { get; set; }
    public Guid? ReplacedByTokenId { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void Revoke(DateTime nowUtc, string reason, Guid? replacedBy = null)
    {
        if (RevokedAtUtc is not null) return;
        RevokedAtUtc = nowUtc;
        RevokedReason = reason;
        ReplacedByTokenId = replacedBy;
    }
}
