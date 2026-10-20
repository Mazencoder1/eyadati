using ClinicSaaS.Api.Errors;
using ClinicSaaS.Api.Infrastructure;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Auth;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ClinicSaaS.Api.Controllers;

/// <summary>
/// Authentication. Web clients get the refresh token only as an httpOnly, Secure, SameSite=Strict cookie scoped
/// to this path and can only present it that way; native clients (header <c>X-Client-Type: mobile</c>) receive it in
/// the body and present it there. A request that asks for native mode but comes from a browser is refused with 403
/// CLIENT_TYPE_NOT_ALLOWED before it does anything (see <see cref="NativeClient"/>), so script running in the web app
/// can never obtain a refresh token.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
[RejectBrowserNativeMode]
public sealed class AuthController(AuthService auth, MfaService mfa, ICurrentUser currentUser, ClinicContext clinic, TenantAccessResolver access) : ControllerBase
{
    private const string RefreshCookie = "rt";
    private const string CookiePath = "/api/v1/auth";

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> Register(RegisterClinicRequest request, CancellationToken ct)
    {
        var result = await auth.RegisterClinicAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<SessionResponse>(ToSession(result)));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> Login(LoginRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<SessionResponse>(ToSession(await auth.LoginAsync(request, ct))));

    [HttpPost("refresh")]
    [RequireTrustedOrigin]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Refresh)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> Refresh([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        var token = PresentedRefreshToken(request);
        try
        {
            return Ok(new ApiResponse<SessionResponse>(ToSession(await auth.RefreshAsync(token ?? "", ct))));
        }
        catch (UnauthorizedException)
        {
            ClearCookie();
            throw;
        }
    }

    [HttpPost("logout")]
    [RequireTrustedOrigin]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request, CancellationToken ct)
    {
        await auth.LogoutAsync(PresentedRefreshToken(request), ct);
        ClearCookie();
        return NoContent();
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(request, ct);
        return Accepted();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(request, ct);
        ClearCookie();
        return NoContent();
    }

    [HttpPost("verify-email")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> VerifyEmail(VerifyEmailRequest request, CancellationToken ct)
    {
        await auth.VerifyEmailAsync(request, ct);
        return NoContent();
    }

    [HttpPost("resend-verification")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> ResendVerification(CancellationToken ct)
    {
        await auth.ResendVerificationAsync(currentUser.UserId ?? throw new UnauthorizedException(), ct);
        return Accepted();
    }

    [HttpPost("accept-invitation")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<IActionResult> AcceptInvitation(AcceptInvitationRequest request, CancellationToken ct)
    {
        await auth.AcceptInvitationAsync(request, ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Sensitive)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<SessionResponse>(ToSession(await auth.ChangePasswordAsync(request, ct))));

    /// <summary>Clinics the signed-in user can switch to.</summary>
    [HttpGet("tenants")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<TenantSummary>>>> MyTenants(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<TenantSummary>>(await auth.ListMyTenantsAsync(ct)));

    [HttpPost("switch-tenant")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> SwitchTenant(SwitchTenantRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<SessionResponse>(ToSession(await auth.SwitchTenantAsync(request.TenantId, ct))));

    [HttpGet("sessions")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<SessionInfo>>>> Sessions(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<SessionInfo>>(await auth.ListSessionsAsync(ct)));

    [HttpDelete("sessions/{sessionId:guid}")]
    [Authorize]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    {
        await auth.RevokeSessionAsync(sessionId, ct);
        return NoContent();
    }

    /// <summary>
    /// Who am I — from the token, plus the caller's own doctor profile in this clinic (looked up by the token's
    /// membership, so the client never has to guess or supply it).
    /// </summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<MeResponse>>> Me(CancellationToken ct) => Ok(new ApiResponse<MeResponse>(new MeResponse(
        currentUser.UserId!.Value, currentUser.TenantId, currentUser.PlatformRole.ToString(),
        currentUser.Permissions.Order().ToList(), currentUser.BranchIds,
        currentUser.MembershipId, currentUser.TenantId is null ? null : await clinic.CallerDoctorIdAsync(ct),
        currentUser.TenantId is { } tenantId ? (await access.ResolveAsync(tenantId, ct)).ToString() : null)));

    // ------------------------------------------------------------------ platform-admin MFA
    // Login answers 409 MFA_REQUIRED / MFA_ENROLLMENT_REQUIRED for a platform account; nothing below issues a session
    // before the second factor (or a completed enrolment) has been proven.

    /// <summary>Begins enrolment. The response carries the setup secret — shown once, never cached or logged.</summary>
    [HttpPost("mfa/enroll/start")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<EnrollStartResult>>> MfaEnrollStart(EnrollStartRequest request, CancellationToken ct)
    {
        var result = await mfa.StartEnrollmentAsync(request, ct);
        NoStore();
        return Ok(new ApiResponse<EnrollStartResult>(result));
    }

    /// <summary>Proves the authenticator works, stores it, ends older sessions and returns the first real session + recovery codes.</summary>
    [HttpPost("mfa/enroll/complete")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<MfaEnrollmentResponse>>> MfaEnrollComplete(EnrollCompleteRequest request, CancellationToken ct)
    {
        var result = await mfa.CompleteEnrollmentAsync(request, ct);
        NoStore();
        return Ok(new ApiResponse<MfaEnrollmentResponse>(new MfaEnrollmentResponse(ToSession(result.Session), result.RecoveryCodes)));
    }

    /// <summary>Second step of sign-in: a TOTP or a recovery code, with the challenge token from login.</summary>
    [HttpPost("mfa/verify")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<SessionResponse>>> MfaVerify(MfaVerifyRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<SessionResponse>(ToSession(await mfa.VerifyAsync(request, ct))));

    /// <summary>Replaces all recovery codes (current TOTP required). Does not disable MFA.</summary>
    [HttpPost("mfa/recovery-codes/regenerate")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<ActionResult<ApiResponse<RecoveryCodesResponse>>> MfaRegenerateRecoveryCodes(RegenerateRecoveryCodesRequest request, CancellationToken ct)
    {
        var codes = await mfa.RegenerateRecoveryCodesAsync(request, ct);
        NoStore();
        return Ok(new ApiResponse<RecoveryCodesResponse>(new RecoveryCodesResponse(codes)));
    }

    /// <summary>Removes the caller's authenticator and ends every session, this one included. The next sign-in enrols again.</summary>
    [HttpPost("mfa/remove")]
    [Authorize]
    [EnableRateLimiting(RateLimitPolicies.Auth)]
    public async Task<IActionResult> MfaRemove(RemoveMfaRequest request, CancellationToken ct)
    {
        await mfa.RemoveAsync(request, ct);
        ClearCookie();
        return NoContent();
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    // ------------------------------------------------------------------ helpers

    // Browsers asking for native mode never get this far ([RejectBrowserNativeMode]).
    private bool IsNativeClient => NativeClient.IsRequested(Request);

    /// <summary>One transport per client type: native → body, browser → the httpOnly cookie only.</summary>
    private string? PresentedRefreshToken(RefreshRequest? body) =>
        IsNativeClient ? body?.RefreshToken : Request.Cookies[RefreshCookie];

    private SessionResponse ToSession(AuthResult r)
    {
        if (!IsNativeClient)
        {
            Response.Cookies.Append(RefreshCookie, r.RefreshToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = CookiePath,
                Expires = r.RefreshTokenExpiresAtUtc,
                IsEssential = true,
            });
        }
        return new SessionResponse(r.AccessToken, r.AccessTokenExpiresAtUtc, IsNativeClient ? r.RefreshToken : null,
            r.User, r.Tenant, r.Permissions);
    }

    private void ClearCookie() =>
        Response.Cookies.Delete(RefreshCookie, new CookieOptions { Path = CookiePath, Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict });
}

public sealed record RefreshRequest(string? RefreshToken);

public sealed record SessionResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string? RefreshToken,
    UserSummary User,
    TenantSummary? Tenant,
    IReadOnlyCollection<string> Permissions);

/// <summary>The first session of a newly enrolled platform admin, plus the one-time recovery codes (shown once).</summary>
public sealed record MfaEnrollmentResponse(SessionResponse Session, IReadOnlyList<string> RecoveryCodes);

public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

public sealed record MeResponse(
    Guid UserId, Guid? TenantId, string PlatformRole, IReadOnlyCollection<string> Permissions, IReadOnlyList<Guid> BranchIds,
    Guid? MembershipId, Guid? DoctorId,
    /// <summary>"Full" | "ReadOnly" (subscription ended) | "Suspended" — the same rule the API enforces per request.</summary>
    string? Access);
