using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Identity;

public enum VerificationPurpose { EmailVerification, PasswordReset, PhoneVerification, LoginOtp, Invitation }

/// <summary>One-time code / link token for email verification, password reset and OTP. Only a hash is stored.</summary>
public sealed class VerificationCode : Entity
{
    public const int MaxAttempts = 5;

    public Guid UserId { get; set; }
    public VerificationPurpose Purpose { get; set; }
    /// <summary>The email or phone the code was sent to.</summary>
    public required string Target { get; set; }
    public required string CodeHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsUsable(DateTime nowUtc) => ConsumedAtUtc is null && ExpiresAtUtc > nowUtc && Attempts < MaxAttempts;
}
