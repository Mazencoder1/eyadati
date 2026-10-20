using ClinicSaaS.Domain.Common;

namespace ClinicSaaS.Domain.Identity;

public enum PlatformRole { None, SuperAdmin, Support, BillingAdmin }

/// <summary>
/// A global identity (one per email). Access to a clinic comes from a <see cref="TenantMembership"/>, so one
/// person (e.g. a doctor) can work in several clinics without duplicate accounts.
/// </summary>
public sealed class User : AuditableEntity, IConcurrencyAware
{
    public required string Email { get; set; }
    public required string NormalizedEmail { get; set; }
    public string PasswordHash { get; set; } = "";
    public required string FullName { get; set; }
    public string? PhoneNumber { get; set; }
    public bool EmailConfirmed { get; set; }
    public bool PhoneNumberConfirmed { get; set; }
    public bool IsActive { get; set; } = true;
    public PlatformRole PlatformRole { get; set; } = PlatformRole.None;
    public string PreferredLocale { get; set; } = "ar";

    public int AccessFailedCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    /// <summary>Rotated on password change / security events; tokens carrying an old stamp are rejected on refresh.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime? LastLoginAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<TenantMembership> Memberships { get; set; } = [];

    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc is { } end && end > nowUtc;

    /// <summary>Counts a failed login; locks the account after <paramref name="maxAttempts"/> consecutive failures.</summary>
    public void RegisterFailedLogin(DateTime nowUtc, int maxAttempts, TimeSpan lockoutDuration)
    {
        AccessFailedCount++;
        if (AccessFailedCount >= maxAttempts)
        {
            LockoutEndUtc = nowUtc.Add(lockoutDuration);
            AccessFailedCount = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTime nowUtc)
    {
        AccessFailedCount = 0;
        LockoutEndUtc = null;
        LastLoginAtUtc = nowUtc;
    }

    public void ChangePasswordHash(string newHash)
    {
        PasswordHash = newHash;
        SecurityStamp = Guid.NewGuid().ToString("N");
        AccessFailedCount = 0;
        LockoutEndUtc = null;
    }
}
