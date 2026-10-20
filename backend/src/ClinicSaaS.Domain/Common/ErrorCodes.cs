namespace ClinicSaaS.Domain.Common;

/// <summary>Stable, client-facing error codes. Messages live in the localization catalog, keyed by these.</summary>
public static class ErrorCodes
{
    // generic
    public const string Validation = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string Forbidden = "FORBIDDEN";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Conflict = "CONFLICT";
    public const string ConcurrencyConflict = "CONCURRENCY_CONFLICT";
    public const string RateLimited = "RATE_LIMITED";
    public const string Internal = "INTERNAL_ERROR";
    /// <summary>The database (or a lock) was briefly unavailable; nothing was retried automatically. 503 + Retry-After.</summary>
    public const string TemporarilyUnavailable = "TEMPORARILY_UNAVAILABLE";

    // auth
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string AccountDisabled = "ACCOUNT_DISABLED";
    public const string EmailNotVerified = "EMAIL_NOT_VERIFIED";
    public const string InvalidRefreshToken = "INVALID_REFRESH_TOKEN";
    /// <summary>A browser asked for the native-client (refresh token in the body) mode, which is never allowed from a browser.</summary>
    public const string ClientTypeNotAllowed = "CLIENT_TYPE_NOT_ALLOWED";
    public const string InvalidOrExpiredToken = "INVALID_OR_EXPIRED_TOKEN";
    public const string EmailTaken = "EMAIL_TAKEN";
    public const string TenantSelectionRequired = "TENANT_SELECTION_REQUIRED";
    public const string NoActiveMembership = "NO_ACTIVE_MEMBERSHIP";
    public const string TenantSuspended = "TENANT_SUSPENDED";
    /// <summary>The clinic's subscription period has ended: the clinic is read-only until it is extended.</summary>
    public const string SubscriptionExpired = "SUBSCRIPTION_EXPIRED";
    public const string PlanNotFound = "PLAN_NOT_FOUND";
    public const string WeakPassword = "WEAK_PASSWORD";

    // tenancy
    public const string CrossTenantAccess = "CROSS_TENANT_ACCESS";
    public const string TenantRequired = "TENANT_REQUIRED";

    // saas
    public const string UsageLimitReached = "USAGE_LIMIT_REACHED";
    public const string FeatureNotAvailable = "FEATURE_NOT_AVAILABLE";

    // clinical
    public const string PatientNotFound = "PATIENT_NOT_FOUND";
    public const string DuplicatePatientIdentity = "DUPLICATE_PATIENT_IDENTITY";
    public const string AppointmentNotFound = "APPOINTMENT_NOT_FOUND";
    public const string InvalidStatusTransition = "INVALID_STATUS_TRANSITION";
    public const string CancellationReasonRequired = "CANCELLATION_REASON_REQUIRED";
    public const string SlotTaken = "SLOT_TAKEN";
    public const string OutsideWorkingHours = "OUTSIDE_WORKING_HOURS";
    public const string SlotInPast = "SLOT_IN_PAST";
    public const string AppointmentNotEditable = "APPOINTMENT_NOT_EDITABLE";
    public const string NoShowBeforeStart = "NO_SHOW_BEFORE_START";
    public const string DoctorNotFound = "DOCTOR_NOT_FOUND";
    public const string DoctorInactive = "DOCTOR_INACTIVE";
    public const string DoctorProfileExists = "DOCTOR_PROFILE_EXISTS";
    public const string ScheduleOverlap = "SCHEDULE_OVERLAP";
    public const string ServiceNotFound = "SERVICE_NOT_FOUND";
    public const string VisitNotFound = "VISIT_NOT_FOUND";
    public const string VisitAlreadyRecorded = "VISIT_ALREADY_RECORDED";
    public const string VisitCompleted = "VISIT_COMPLETED";
    public const string AppointmentNotCheckedIn = "APPOINTMENT_NOT_CHECKED_IN";
    public const string PrescriptionNotFound = "PRESCRIPTION_NOT_FOUND";
    /// <summary>D8: only the visit's own doctor, signed in, may issue a prescription for it.</summary>
    public const string PrescriberNotTreatingDoctor = "PRESCRIBER_NOT_TREATING_DOCTOR";
    public const string VisitNotTreatingDoctor = "VISIT_NOT_TREATING_DOCTOR";
    /// <summary>D6: a patient with clinical history or appointments is never deleted.</summary>
    public const string PatientHasHistory = "PATIENT_HAS_HISTORY";
    public const string FileNotFound = "FILE_NOT_FOUND";
    /// <summary>The file exists but is quarantined: not scanned clean (Pending/Scanning/Failed/Rejected).</summary>
    public const string FileNotAvailable = "FILE_NOT_AVAILABLE";
    public const string FileStorageUnavailable = "FILE_STORAGE_UNAVAILABLE";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";

    // organization
    public const string BranchNotFound = "BRANCH_NOT_FOUND";
    public const string BranchRequired = "BRANCH_REQUIRED";
    public const string BranchAccessDenied = "BRANCH_ACCESS_DENIED";
    public const string MainBranchProtected = "MAIN_BRANCH_PROTECTED";
    public const string RoleNotFound = "ROLE_NOT_FOUND";
    public const string RoleInUse = "ROLE_IN_USE";
    public const string RoleNameTaken = "ROLE_NAME_TAKEN";
    public const string SystemRoleProtected = "SYSTEM_ROLE_PROTECTED";
    public const string PermissionEscalation = "PERMISSION_ESCALATION";
    public const string StaffNotFound = "STAFF_NOT_FOUND";
    public const string StaffAlreadyMember = "STAFF_ALREADY_MEMBER";
    public const string InvitationSignInRequired = "INVITATION_SIGN_IN_REQUIRED";
    public const string OwnerProtected = "OWNER_PROTECTED";
    public const string CannotChangeSelf = "CANNOT_CHANGE_SELF";
    public const string TenantNotFound = "TENANT_NOT_FOUND";
    public const string SessionNotFound = "SESSION_NOT_FOUND";

    // platform-admin MFA
    /// <summary>Password was right for a platform account that has an authenticator: a code is needed (409, carries the challenge token).</summary>
    public const string MfaRequired = "MFA_REQUIRED";
    /// <summary>Password was right for a platform account without an authenticator: it must enrol first (409, carries the enrolment token).</summary>
    public const string MfaEnrollmentRequired = "MFA_ENROLLMENT_REQUIRED";
    /// <summary>Wrong or already-used TOTP / recovery code. One code for every kind of failure, so nothing is disclosed.</summary>
    public const string InvalidMfaCode = "INVALID_MFA_CODE";
    /// <summary>The challenge / enrolment token is expired, tampered with, for another purpose or for another user.</summary>
    public const string InvalidMfaToken = "INVALID_MFA_TOKEN";
    public const string MfaAlreadyEnrolled = "MFA_ALREADY_ENROLLED";
}
