using ClinicSaaS.Application.Common;
using FluentValidation;

namespace ClinicSaaS.Application.Auth;

/// <summary>Begins enrolment. The token comes from the 409 MFA_ENROLLMENT_REQUIRED login answer.</summary>
public sealed record EnrollStartRequest(string EnrollmentToken);

/// <summary>
/// What the user needs to add the authenticator. Shown exactly once, over a <c>no-store</c> response, and never
/// logged. <see cref="PendingToken"/> carries the not-yet-confirmed secret (encrypted) back to complete the enrolment.
/// </summary>
public sealed record EnrollStartResult(string Secret, string SetupUri, string PendingToken, DateTime ExpiresAtUtc);

public sealed record EnrollCompleteRequest(string PendingToken, string Code);

/// <summary>The session that exists only now, plus the recovery codes — shown once.</summary>
public sealed record EnrollCompleteResult(AuthResult Session, IReadOnlyList<string> RecoveryCodes);

/// <summary>Exactly one of <see cref="TotpCode"/> / <see cref="RecoveryCode"/>.</summary>
public sealed record MfaVerifyRequest(string ChallengeToken, string? TotpCode, string? RecoveryCode);

/// <summary>Recovery codes cannot authorise regenerating recovery codes: only a current TOTP can.</summary>
public sealed record RegenerateRecoveryCodesRequest(string TotpCode);

public sealed record RemoveMfaRequest(string CurrentPassword, string? TotpCode, string? RecoveryCode);

public sealed class EnrollStartValidator : AbstractValidator<EnrollStartRequest>
{
    public EnrollStartValidator() => RuleFor(x => x.EnrollmentToken).RequiredText().MaxLen(2048);
}

public sealed class EnrollCompleteValidator : AbstractValidator<EnrollCompleteRequest>
{
    public EnrollCompleteValidator()
    {
        RuleFor(x => x.PendingToken).RequiredText().MaxLen(2048);
        RuleFor(x => x.Code).RequiredText().MaxLen(16);
    }
}

public sealed class MfaVerifyValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyValidator()
    {
        RuleFor(x => x.ChallengeToken).RequiredText().MaxLen(2048);
        RuleFor(x => x.TotpCode).MaxLen(16);
        RuleFor(x => x.RecoveryCode).MaxLen(32);
        RuleFor(x => x).Must(x => string.IsNullOrWhiteSpace(x.TotpCode) != string.IsNullOrWhiteSpace(x.RecoveryCode))
            .OverridePropertyName("code").WithMessage(ValidationCodes.Required);
    }
}

public sealed class RegenerateRecoveryCodesValidator : AbstractValidator<RegenerateRecoveryCodesRequest>
{
    public RegenerateRecoveryCodesValidator() => RuleFor(x => x.TotpCode).RequiredText().MaxLen(16);
}

public sealed class RemoveMfaValidator : AbstractValidator<RemoveMfaRequest>
{
    public RemoveMfaValidator()
    {
        RuleFor(x => x.CurrentPassword).RequiredText().MaxLen(256);
        RuleFor(x => x.TotpCode).MaxLen(16);
        RuleFor(x => x.RecoveryCode).MaxLen(32);
        RuleFor(x => x).Must(x => string.IsNullOrWhiteSpace(x.TotpCode) != string.IsNullOrWhiteSpace(x.RecoveryCode))
            .OverridePropertyName("code").WithMessage(ValidationCodes.Required);
    }
}
