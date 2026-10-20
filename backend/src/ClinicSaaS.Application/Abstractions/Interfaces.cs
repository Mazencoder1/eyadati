using System.Data;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Clinical;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicSaaS.Application.Abstractions;

/// <summary>The authenticated principal of the current request, built from the access token only.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    Guid? TenantId { get; }
    Guid? MembershipId { get; }
    /// <summary>The refresh-token family this access token was issued for (one per signed-in device).</summary>
    Guid? SessionId { get; }
    PlatformRole PlatformRole { get; }
    IReadOnlySet<string> Permissions { get; }
    /// <summary>Branches the member is restricted to; empty = all branches.</summary>
    IReadOnlyList<Guid> BranchIds { get; }
    bool HasPermission(string permission);
}

/// <summary>Transport-level facts about the request, used for audit and security decisions.</summary>
public interface IRequestContext
{
    string? IpAddress { get; }
    string? UserAgent { get; }
    string CorrelationId { get; }
    /// <summary>"ar" or "en".</summary>
    string Language { get; }
}

/// <summary>
/// The tenant the data layer is scoped to. Defaults to the current user's tenant; system flows (signup,
/// background jobs) open an explicit scope. No tenant ⇒ tenant-owned queries return nothing (deny by default).
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    /// <summary>Scope the data layer to <paramref name="tenantId"/> until disposed.</summary>
    IDisposable UseTenant(Guid tenantId);
}

public interface IAppDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Branch> Branches { get; }
    DbSet<Plan> Plans { get; }
    DbSet<Subscription> Subscriptions { get; }
    DbSet<TenantFeatureOverride> TenantFeatureOverrides { get; }
    DbSet<User> Users { get; }
    DbSet<TenantMembership> TenantMemberships { get; }
    DbSet<Role> Roles { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<VerificationCode> VerificationCodes { get; }
    DbSet<UserTotpFactor> UserTotpFactors { get; }
    DbSet<UserRecoveryCode> UserRecoveryCodes { get; }
    DbSet<Patient> Patients { get; }
    DbSet<DoctorProfile> Doctors { get; }
    DbSet<DoctorSchedule> DoctorSchedules { get; }
    DbSet<ClinicService> Services { get; }
    DbSet<Appointment> Appointments { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<NumberSequence> NumberSequences { get; }
    DbSet<Visit> Visits { get; }
    DbSet<Prescription> Prescriptions { get; }
    DbSet<FileAttachment> FileAttachments { get; }

    Task<int> SaveChangesAsync(CancellationToken ct = default);

    /// <summary>Runs <paramref name="operation"/> in a transaction, retry-safe under the SQL Server execution strategy.</summary>
    Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, IsolationLevel isolation = IsolationLevel.ReadCommitted, CancellationToken ct = default);

    /// <summary>
    /// Takes an exclusive, transaction-scoped application lock (sp_getapplock) so check-then-insert sequences on
    /// the same resource (e.g. one doctor's calendar) run one at a time. Must be called inside a transaction.
    /// </summary>
    Task AcquireLockAsync(string resource, CancellationToken ct = default);

    /// <summary>Sets the concurrency token the client last saw, so a stale update raises a conflict.</summary>
    void SetOriginalRowVersion<T>(T entity, byte[] rowVersion) where T : class, IConcurrencyAware;
}

public interface IClock
{
    DateTime UtcNow { get; }
}

public interface IPasswordHasher
{
    string Hash(User user, string password);
    bool Verify(User user, string hash, string password);
}

public sealed record AccessToken(string Token, DateTime ExpiresAtUtc);

public sealed record TokenClaims(
    Guid UserId,
    string Email,
    string FullName,
    Guid? TenantId,
    Guid? MembershipId,
    PlatformRole PlatformRole,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyCollection<Guid> BranchIds,
    Guid SessionId);

public interface ITokenService
{
    AccessToken CreateAccessToken(TokenClaims claims);
    /// <summary>Cryptographically random opaque token (raw value, to be sent to the client once).</summary>
    string CreateOpaqueToken();
    /// <summary>SHA-256 hex of an opaque token — the only form persisted.</summary>
    string Hash(string opaqueToken);
}

/// <summary>What a short-lived MFA token is for. A token only opens the step it was issued for.</summary>
public enum MfaTokenPurpose
{
    /// <summary>Password was right and an authenticator exists: lets the holder submit a code (POST /auth/mfa/verify).</summary>
    Challenge,
    /// <summary>Password was right and no authenticator exists: lets the holder begin enrolment.</summary>
    EnrollmentStart,
    /// <summary>Enrolment begun: carries the not-yet-confirmed secret until the user proves possession.</summary>
    EnrollmentPending,
}

/// <param name="UserId">The account the token was issued for.</param>
/// <param name="TenantId">The clinic the user asked to sign in to (finished after MFA), if any.</param>
/// <param name="Data">Purpose-specific payload (the pending secret for <see cref="MfaTokenPurpose.EnrollmentPending"/>).</param>
public sealed record MfaTokenPayload(Guid UserId, Guid? TenantId, string? Data = null);

/// <summary>
/// Protects the TOTP secret at rest and issues the stateless, short-lived MFA tokens. Backed by ASP.NET Core Data
/// Protection (authenticated encryption, explicit purposes, key ring outside the database) — no custom cryptography.
/// Nothing here throws on tampered or foreign input: it returns null, and callers fail closed.
/// </summary>
public interface IMfaProtector
{
    /// <summary>Encrypts a TOTP secret, bound to its owner (a blob copied to another user's row will not decrypt).</summary>
    byte[] ProtectSecret(Guid userId, byte[] secret);

    /// <summary>The secret, or null when the blob is not ours, was tampered with, belongs to another user or the key is gone.</summary>
    byte[]? TryUnprotectSecret(Guid userId, byte[] protectedSecret);

    string ProtectToken(MfaTokenPurpose purpose, MfaTokenPayload payload, DateTime expiresAtUtc);

    /// <summary>The payload, or null when the token is expired, tampered with, forged, or issued for another purpose.</summary>
    MfaTokenPayload? TryUnprotectToken(MfaTokenPurpose purpose, string token, DateTime nowUtc);
}

public interface IAuditLogger
{
    /// <summary>Adds an audit row to the current unit of work (saved with the business change).</summary>
    void Add(string action, string? entityType = null, string? entityId = null, AuditResult result = AuditResult.Success, object? details = null, Guid? tenantId = null, Guid? userId = null);
}

public interface ISequenceGenerator
{
    /// <summary>Next value of a per-tenant gap-free sequence. Must run inside a transaction.</summary>
    Task<long> NextAsync(Guid tenantId, string sequenceName, CancellationToken ct = default);
}

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string? TextBody = null);

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Private blob storage for attachment bytes. Keys are generated by the server (never derived from a client value)
/// and are opaque to callers; implementations must refuse anything that is not such a key. Swapping the local-disk
/// provider for a cloud one changes only the registration, not the business logic.
/// </summary>
public interface IFileStorage
{
    /// <summary>Stores <paramref name="content"/> under a new <paramref name="key"/>. Never overwrites an existing object.</summary>
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>Opens the stored object for reading, or null if it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>Removes the object if present (used to undo a save whose metadata could not be recorded).</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
