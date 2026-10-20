using ClinicSaaS.Application.Common;
using FluentValidation;

namespace ClinicSaaS.Application.Auth;

public sealed record RegisterClinicRequest(
    string ClinicName,
    string? Slug,
    string City,
    string ClinicPhone,
    string OwnerFullName,
    string Email,
    string Password,
    string? OwnerMobile,
    bool AcceptTerms);

public sealed record LoginRequest(string Email, string Password, Guid? TenantId = null);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

/// <summary>
/// Accepting a staff invitation. A NEW account (no password yet) accepts anonymously and must choose
/// <see cref="NewPassword"/>. An EXISTING account must be signed in as the invited account; its password is never
/// changed through an invitation (<see cref="NewPassword"/> is ignored).
/// </summary>
public sealed record AcceptInvitationRequest(string Token, string? NewPassword);

/// <summary>
/// Invitation links carry the id of the seat they were issued for (<c>{membershipId:N}.{random}</c>). Only the hash of
/// the whole value is stored, so the seat id can't be swapped without invalidating the link.
/// </summary>
public static class InvitationTokens
{
    public static string Compose(Guid membershipId, string opaque) => $"{membershipId:N}.{opaque}";

    public static Guid? MembershipIdOf(string raw) =>
        raw.Length > 33 && raw[32] == '.' && Guid.TryParseExact(raw[..32], "N", out var id) ? id : null;
}

public sealed record VerifyEmailRequest(string Token);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record SwitchTenantRequest(Guid TenantId);

public sealed record SessionInfo(Guid SessionId, Guid? TenantId, DateTime LastActivityAtUtc, string? IpAddress, string? UserAgent, bool IsCurrent);

public sealed record UserSummary(Guid Id, string FullName, string Email, bool EmailConfirmed, string PlatformRole, string PreferredLocale);

public sealed record TenantSummary(Guid Id, string Name, string Slug);

/// <summary>Result of login/refresh. The refresh token is set as an httpOnly cookie by the API, not exposed to JS.</summary>
public sealed record AuthResult(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    UserSummary User,
    TenantSummary? Tenant,
    IReadOnlyCollection<string> Permissions);

public sealed class AuthOptions
{
    public const string Section = "Auth";
    public int MaxFailedAccessAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    /// <summary>Lockout after <see cref="MaxFailedAccessAttempts"/> wrong MFA codes. Longer than a password lockout: 6-digit codes are guessable.</summary>
    public int MfaLockoutMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 14;
    public int PasswordResetTokenMinutes { get; set; } = 30;
    public int EmailVerificationTokenHours { get; set; } = 48;
    /// <summary>When true, login is refused until the email is verified.</summary>
    public bool RequireConfirmedEmail { get; set; }
    /// <summary>Base URL of the web app, used to build links in emails.</summary>
    public string AppBaseUrl { get; set; } = "http://localhost:3000";
}

public sealed class RegisterClinicValidator : AbstractValidator<RegisterClinicRequest>
{
    public RegisterClinicValidator()
    {
        RuleFor(x => x.ClinicName).RequiredText().MaxLen(200);
        RuleFor(x => x.Slug).Matches("^[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])$").WithMessage(ValidationCodes.InvalidSlug)
            .When(x => !string.IsNullOrWhiteSpace(x.Slug));
        RuleFor(x => x.City).RequiredText().MaxLen(100);
        RuleFor(x => x.ClinicPhone).RequiredText().SaudiMobile();
        RuleFor(x => x.OwnerFullName).RequiredText().MaxLen(200);
        RuleFor(x => x.Email).RequiredText().ValidEmail();
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.OwnerMobile).SaudiMobile();
        RuleFor(x => x.AcceptTerms).Equal(true).WithMessage(ValidationCodes.Required);
    }
}

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).RequiredText().MaxLen(256);
        RuleFor(x => x.Password).RequiredText().MaxLen(128);
    }
}

public sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordValidator() => RuleFor(x => x.Email).RequiredText().ValidEmail();
}

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).RequiredText().MaxLen(200);
        RuleFor(x => x.NewPassword).StrongPassword();
    }
}

public sealed class AcceptInvitationValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationValidator()
    {
        RuleFor(x => x.Token).RequiredText().MaxLen(200);
        RuleFor(x => x.NewPassword).StrongPassword().When(x => x.NewPassword is not null);
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).RequiredText().MaxLen(128);
        RuleFor(x => x.NewPassword).StrongPassword()
            .NotEqual(x => x.CurrentPassword).WithMessage(ValidationCodes.InvalidValue);
    }
}

public sealed class VerifyEmailValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailValidator() => RuleFor(x => x.Token).RequiredText().MaxLen(200);
}
