using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Identity;

/// <summary>
/// A user's confirmed TOTP authenticator (platform administrators only). A row exists only after the user proved
/// possession of the authenticator, so there is no "pending" or "disabled" state: removing MFA deletes the row.
/// Platform-owned (no TenantId). At most one row per user.
/// </summary>
public sealed class UserTotpFactor : Entity
{
    public Guid UserId { get; set; }

    /// <summary>The TOTP secret, encrypted with ASP.NET Core Data Protection and bound to <see cref="UserId"/>. Never plaintext.</summary>
    public required byte[] SecretProtected { get; set; }

    /// <summary>The newest accepted 30-second time step. A code is accepted only for a strictly greater step (replay protection).</summary>
    public long? LastUsedTimeStep { get; set; }

    /// <summary>When the user proved possession of the authenticator, i.e. when MFA was enabled.</summary>
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// One of a user's one-time recovery codes. Only the SHA-256 of the code is stored. Platform-owned (no TenantId).
/// </summary>
public sealed class UserRecoveryCode : Entity
{
    public Guid UserId { get; set; }
    public required string CodeHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}
