namespace ClinicSaaS.Domain.Auditing;

public enum AuditResult { Success, Failure, Denied }

/// <summary>
/// Append-only record of a sensitive action: who, what, when, tenant, IP, device, result.
/// Never stores passwords, tokens, OTPs or clinical content — only identifiers and changed field names.
/// </summary>
public sealed class AuditLog
{
    public long Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public required string Action { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public AuditResult Result { get; set; } = AuditResult.Success;
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? CorrelationId { get; set; }
    /// <summary>Small JSON context, e.g. {"fields":["MobileNumber"]}. Never values of sensitive fields.</summary>
    public string? Details { get; set; }
}

public static class AuditActions
{
    public const string LoginSucceeded = "auth.login.succeeded";
    public const string LoginFailed = "auth.login.failed";
    public const string AccountLocked = "auth.account.locked";
    public const string RefreshTokenReuse = "auth.refresh.reuse_detected";
    public const string Logout = "auth.logout";
    public const string PasswordResetRequested = "auth.password_reset.requested";
    public const string PasswordChanged = "auth.password.changed";
    public const string EmailVerified = "auth.email.verified";

    public const string TenantRegistered = "tenant.registered";

    public const string PatientCreated = "patient.created";
    public const string PatientUpdated = "patient.updated";
    public const string PatientViewed = "patient.viewed";
    public const string PatientDeleted = "patient.deleted";

    public const string UserCreated = "user.created";
    public const string StaffInvited = "staff.invited";
    public const string StaffUpdated = "staff.updated";
    public const string StaffDisabled = "staff.disabled";
    public const string StaffEnabled = "staff.enabled";
    public const string RoleCreated = "role.created";
    public const string RoleDeleted = "role.deleted";
    public const string BranchCreated = "branch.created";
    public const string BranchUpdated = "branch.updated";
    public const string SettingsUpdated = "settings.updated";
    public const string DoctorCreated = "doctor.created";
    public const string DoctorUpdated = "doctor.updated";
    public const string ScheduleUpdated = "doctor.schedule_updated";
    public const string ServiceChanged = "service.changed";
    public const string AppointmentCreated = "appointment.created";
    public const string AppointmentRescheduled = "appointment.rescheduled";
    public const string AppointmentStatusChanged = "appointment.status_changed";
    public const string VisitRecorded = "visit.recorded";
    public const string VisitUpdated = "visit.updated";
    public const string VisitViewed = "visit.viewed";
    public const string VisitCompleted = "visit.completed";
    public const string PrescriptionIssued = "prescription.issued";
    public const string PrescriptionViewed = "prescription.viewed";
    public const string FileUploaded = "file.uploaded";
    public const string FileDownloaded = "file.downloaded";
    public const string FileViewed = "file.viewed";
    public const string FilesListed = "file.listed";
    public const string FileDeleted = "file.deleted";
    public const string TenantSwitched = "auth.tenant.switched";
    public const string SessionRevoked = "auth.session.revoked";
    public const string InvitationAccepted = "auth.invitation.accepted";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantReactivated = "tenant.reactivated";
    public const string RoleChanged = "role.changed";
    public const string SubscriptionChanged = "subscription.changed";
    public const string AccessDenied = "security.access_denied";

    // Platform-admin MFA. Details never carry a secret, a code, a URI or a token — only the kind of attempt.
    public const string MfaChallengeIssued = "auth.mfa.challenge_issued";
    public const string MfaEnrollmentStarted = "auth.mfa.enrollment_started";
    public const string MfaEnrollmentFailed = "auth.mfa.enrollment_failed";
    public const string MfaEnabled = "auth.mfa.enabled";
    public const string MfaChallengeFailed = "auth.mfa.challenge_failed";
    public const string MfaChallengeSucceeded = "auth.mfa.challenge_succeeded";
    public const string MfaRecoveryCodeUsed = "auth.mfa.recovery_code_used";
    public const string MfaRecoveryCodesRegenerated = "auth.mfa.recovery_codes_regenerated";
    /// <summary>The user removed their own authenticator (all sessions revoked; next sign-in must enrol again).</summary>
    public const string MfaDisabled = "auth.mfa.disabled";
    /// <summary>
    /// An operator reset the authenticator out of band (break-glass, docs/operations/platform-admin-mfa.md). The
    /// application has no code path that emits it: the runbook writes this audit row together with the reset.
    /// </summary>
    public const string MfaReset = "auth.mfa.reset";
}
