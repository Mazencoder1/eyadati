using ClinicSaaS.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace ClinicSaaS.Application.Abstractions;

/// <summary>
/// Stable log event ids for monitoring and alerting. A log platform matches on the event id (and the property names of
/// each template), not on message text — see docs/operations/monitoring-and-alerting.md, which lists the alert rules.
/// Change an id, a template property or the classification below only together with that document.
/// </summary>
public static class SecurityLogEvents
{
    /// <summary>A security-relevant audit row was saved. Properties: AuditId, AuditAction, AuditResult, AuditEntityType, AuditUserId, AuditTenantId.</summary>
    public static readonly EventId SecurityAudit = new(4100, "SecurityAudit");
    /// <summary>A request was refused by a rate-limit policy. Properties: RequestMethod, RequestPath (no query string), RateLimitPolicy.</summary>
    public static readonly EventId RateLimited = new(4101, "RateLimited");
    /// <summary>An email could not be handed to the provider after the data was committed. Properties: Purpose, Domain, FailureTypes.</summary>
    public static readonly EventId EmailDeliveryFailed = new(4102, "EmailDeliveryFailed");
    /// <summary>A write that would have crossed tenants was blocked. Always Critical.</summary>
    public static readonly EventId TenantIsolationViolation = new(4103, "TenantIsolationViolation");
    /// <summary>Bootstrap secrets are still configured although the platform account already exists.</summary>
    public static readonly EventId BootstrapSecretStillConfigured = new(4104, "BootstrapSecretStillConfigured");
    /// <summary>A platform admin's TOTP secret could not be decrypted: the Data Protection key ring is missing or wrong. Property: UserId.</summary>
    public static readonly EventId MfaKeyRingUnreadable = new(4105, "MfaKeyRingUnreadable");
    /// <summary>A transient database failure was answered with 503 + Retry-After. Property: Reason.</summary>
    public static readonly EventId DatabaseTemporarilyUnavailable = new(4106, "DatabaseTemporarilyUnavailable");
    /// <summary>An unexpected exception was answered with a generic 500 (details in the exception, never in the response).</summary>
    public static readonly EventId UnhandledException = new(4107, "UnhandledException");

    // Alert-worthy on a single occurrence for platform accounts, or on a rate for everyone (Warning).
    private static readonly HashSet<string> Alerting =
    [
        AuditActions.AccountLocked,
        AuditActions.RefreshTokenReuse,
        AuditActions.MfaChallengeFailed,
        AuditActions.MfaRecoveryCodeUsed,
        AuditActions.MfaRecoveryCodesRegenerated,
        AuditActions.MfaDisabled,
        AuditActions.MfaReset,
    ];

    // Security trail worth having in the central log store too (Information). Clinical reads/writes are deliberately
    // absent: they stay in the audit table only, so the log store never becomes a second record of patient activity.
    private static readonly HashSet<string> Notable =
    [
        AuditActions.LoginFailed,
        AuditActions.PasswordChanged,
        AuditActions.PasswordResetRequested,
        AuditActions.SessionRevoked,
        AuditActions.AccessDenied,
        AuditActions.MfaChallengeIssued,
        AuditActions.MfaChallengeSucceeded,
        AuditActions.MfaEnrollmentStarted,
        AuditActions.MfaEnrollmentFailed,
        AuditActions.MfaEnabled,
        AuditActions.TenantRegistered,
        AuditActions.TenantSuspended,
        AuditActions.TenantReactivated,
        AuditActions.SubscriptionChanged,
        AuditActions.RoleCreated,
        AuditActions.RoleChanged,
        AuditActions.RoleDeleted,
        AuditActions.StaffInvited,
        AuditActions.StaffDisabled,
        AuditActions.StaffEnabled,
        AuditActions.InvitationAccepted,
    ];

    /// <summary>The log level for an audit action, or null when the action is not mirrored to the log.</summary>
    public static LogLevel? LevelFor(string action) =>
        Alerting.Contains(action) ? LogLevel.Warning : Notable.Contains(action) ? LogLevel.Information : null;
}
