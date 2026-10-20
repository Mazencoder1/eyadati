using System.Data;
using System.Net;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Tenancy;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Authorization;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Domain.Platform;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Application.Auth;

public sealed class AuthService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IClock clock,
    IAuditLogger audit,
    IRequestContext request,
    ICurrentUser currentUser,
    IEmailSender email,
    TenantProvisioningService provisioning,
    IOptions<AuthOptions> options,
    IValidator<RegisterClinicRequest> registerValidator,
    IValidator<LoginRequest> loginValidator,
    IValidator<ForgotPasswordRequest> forgotValidator,
    IValidator<ResetPasswordRequest> resetValidator,
    IValidator<VerifyEmailRequest> verifyValidator,
    IValidator<ChangePasswordRequest> changePasswordValidator,
    IValidator<AcceptInvitationRequest> acceptValidator,
    ITenantContext tenantContext,
    IMfaProtector mfaProtector,
    ILogger<AuthService> logger)
{
    private readonly AuthOptions _opt = options.Value;

    // ---------------------------------------------------------------- registration

    public async Task<AuthResult> RegisterClinicAsync(RegisterClinicRequest req, CancellationToken ct)
    {
        await registerValidator.ValidateOrThrowAsync(req, ct);
        var normalizedEmail = User.Normalize(req.Email);

        var (user, membership, tenant, verificationToken) = await db.InTransactionAsync(async c =>
        {
            if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, c))
                throw new ConflictException(ErrorCodes.EmailTaken);

            var owner = new User
            {
                Email = req.Email.Trim(),
                NormalizedEmail = normalizedEmail,
                FullName = req.OwnerFullName.Trim(),
                PhoneNumber = SaudiFormats.NormalizeMobile(req.OwnerMobile),
            };
            owner.PasswordHash = hasher.Hash(owner, req.Password);
            db.Users.Add(owner);

            var provisioned = await provisioning.ProvisionAsync(
                new NewTenantSpec(req.ClinicName, req.Slug, req.City, req.ClinicPhone, owner.Email), owner, c);

            var token = await IssueVerificationCodeAsync(owner, VerificationPurpose.EmailVerification, TimeSpan.FromHours(_opt.EmailVerificationTokenHours), c);
            await db.SaveChangesAsync(c);
            return (owner, provisioned.OwnerMembership, provisioned.Tenant, token);
        }, IsolationLevel.ReadCommitted, ct);

        await SendVerificationEmailAsync(user, verificationToken, ct);
        return await IssueSessionAsync(user, membership, tenant, familyId: null, ct);
    }

    // ---------------------------------------------------------------- login / refresh / logout

    public async Task<AuthResult> LoginAsync(LoginRequest req, CancellationToken ct)
    {
        await loginValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == User.Normalize(req.Email), ct);

        if (user is null)
        {
            // Equalize timing so response time doesn't reveal whether the email exists.
            hasher.Verify(DummyUser, _dummyHash ??= hasher.Hash(DummyUser, "not-a-real-password-1"), req.Password);
            audit.Add(AuditActions.LoginFailed, result: AuditResult.Failure, details: new { reason = "unknown_email" });
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials);
        }

        if (user.IsLockedOut(now)) throw new LockedException(ErrorCodes.AccountLocked);

        if (!hasher.Verify(user, user.PasswordHash, req.Password))
        {
            user.RegisterFailedLogin(now, _opt.MaxFailedAccessAttempts, TimeSpan.FromMinutes(_opt.LockoutMinutes));
            var locked = user.IsLockedOut(now);
            audit.Add(locked ? AuditActions.AccountLocked : AuditActions.LoginFailed, nameof(User), user.Id.ToString(),
                AuditResult.Failure, userId: user.Id);
            await db.SaveChangesAsync(ct);
            throw locked ? new LockedException(ErrorCodes.AccountLocked) : new UnauthorizedException(ErrorCodes.InvalidCredentials);
        }

        if (!user.IsActive) throw new ForbiddenException(ErrorCodes.AccountDisabled);
        if (_opt.RequireConfirmedEmail && !user.EmailConfirmed) throw new ForbiddenException(ErrorCodes.EmailNotVerified);

        // A platform account never gets a session from its password alone: it is handed a challenge (or enrolment)
        // token instead. The failure counter is deliberately NOT reset here — only a completed MFA step resets it.
        if (user.PlatformRole != PlatformRole.None) await ThrowMfaStepAsync(user, req.TenantId, ct);

        var membership = await ResolveLoginMembershipAsync(user, req.TenantId, ct);
        EnsureTenantUsable(membership?.Tenant);

        user.RegisterSuccessfulLogin(now);
        audit.Add(AuditActions.LoginSucceeded, nameof(User), user.Id.ToString(), tenantId: membership?.TenantId, userId: user.Id);
        await db.SaveChangesAsync(ct);

        return await IssueSessionAsync(user, membership, membership?.Tenant, familyId: null, ct);
    }

    /// <summary>Which clinic (if any) a login is for. The rules are the same before and after the MFA step.</summary>
    private async Task<TenantMembership?> ResolveLoginMembershipAsync(User user, Guid? requestedTenantId, CancellationToken ct)
    {
        var memberships = await LoadActiveMembershipsAsync(user.Id, tenantId: null, ct);
        if (requestedTenantId is { } requested)
        {
            return memberships.FirstOrDefault(m => m.TenantId == requested)
                   ?? throw new ForbiddenException(ErrorCodes.NoActiveMembership);
        }
        if (user.PlatformRole != PlatformRole.None)
        {
            // A platform user who names no clinic gets a platform session — never an automatic clinic session, so
            // the two planes never share a token. Clinic work: login with tenantId, or /auth/switch-tenant.
            return null;
        }
        if (memberships.Count > 1)
        {
            throw new ConflictException(ErrorCodes.TenantSelectionRequired,
                memberships.Select(m => new TenantSummary(m.TenantId, m.Tenant!.Name, m.Tenant.Slug)).ToList());
        }
        return memberships.SingleOrDefault() ?? throw new ForbiddenException(ErrorCodes.NoActiveMembership);
    }

    /// <summary>
    /// Password proven, second factor still missing: answers 409 with a short-lived token and creates nothing — no
    /// access token, no refresh token, no cookie, no session family. MFA_REQUIRED when an authenticator exists,
    /// MFA_ENROLLMENT_REQUIRED when the account has to set one up first.
    /// </summary>
    private async Task ThrowMfaStepAsync(User user, Guid? requestedTenantId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var enrolled = await db.UserTotpFactors.AnyAsync(f => f.UserId == user.Id, ct);
        var expires = now.Add(enrolled ? MfaService.ChallengeLifetime : MfaService.EnrollmentLifetime);
        var token = mfaProtector.ProtectToken(enrolled ? MfaTokenPurpose.Challenge : MfaTokenPurpose.EnrollmentStart,
            new MfaTokenPayload(user.Id, requestedTenantId), expires);

        audit.Add(AuditActions.MfaChallengeIssued, nameof(User), user.Id.ToString(),
            details: new { kind = enrolled ? "verify" : "enroll" }, userId: user.Id);
        await db.SaveChangesAsync(ct);

        throw enrolled
            ? new ConflictException(ErrorCodes.MfaRequired, new { challengeToken = token, expiresAtUtc = expires })
            : new ConflictException(ErrorCodes.MfaEnrollmentRequired, new { enrollmentToken = token, expiresAtUtc = expires });
    }

    /// <summary>
    /// Creates the real session after the second factor succeeded (or enrolment was completed). <paramref name="user"/>
    /// is only read; the caller has already reset the failure counter atomically.
    /// </summary>
    internal async Task<AuthResult> CompleteMfaLoginAsync(User user, Guid? requestedTenantId, CancellationToken ct)
    {
        var membership = await ResolveLoginMembershipAsync(user, requestedTenantId, ct);
        EnsureTenantUsable(membership?.Tenant);
        audit.Add(AuditActions.LoginSucceeded, nameof(User), user.Id.ToString(), tenantId: membership?.TenantId, userId: user.Id,
            details: new { mfa = true });
        await db.SaveChangesAsync(ct);
        return await IssueSessionAsync(user, membership, membership?.Tenant, familyId: null, ct);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);
        var now = clock.UtcNow;
        var hash = tokens.Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
                     ?? throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);

        if (stored.RevokedAtUtc is not null)
        {
            // A rotated token was replayed: assume theft and kill the whole session family.
            await RevokeFamilyAsync(stored.FamilyId, "reuse_detected", now, ct);
            audit.Add(AuditActions.RefreshTokenReuse, nameof(RefreshToken), stored.FamilyId.ToString(), AuditResult.Denied,
                tenantId: stored.TenantId, userId: stored.UserId);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);
        }
        if (!stored.IsActive(now)) throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null || !user.IsActive || user.IsLockedOut(now)) throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);

        // A refresh token can never stand in for MFA: a platform account without an authenticator holds no session.
        if (user.PlatformRole != PlatformRole.None && !await db.UserTotpFactors.AnyAsync(f => f.UserId == user.Id, ct))
        {
            await RevokeFamilyAsync(stored.FamilyId, "mfa_required", now, ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);
        }

        TenantMembership? membership = null;
        if (stored.TenantId is { } tenantId)
        {
            membership = (await LoadActiveMembershipsAsync(user.Id, tenantId, ct)).SingleOrDefault()
                         ?? throw new UnauthorizedException(ErrorCodes.InvalidRefreshToken);
            EnsureTenantUsable(membership.Tenant);
        }

        // Permissions are reloaded on every refresh, so role changes apply within one access-token lifetime.
        var result = await IssueSessionAsync(user, membership, membership?.Tenant, stored.FamilyId, ct, rotateFrom: stored);
        return result;
    }

    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var hash = tokens.Hash(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null) return;
        await RevokeFamilyAsync(stored.FamilyId, "logout", clock.UtcNow, ct);
        audit.Add(AuditActions.Logout, nameof(User), stored.UserId.ToString(), tenantId: stored.TenantId, userId: stored.UserId);
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- password reset / email verification

    /// <summary>Always succeeds from the caller's point of view (no account enumeration).</summary>
    public async Task ForgotPasswordAsync(ForgotPasswordRequest req, CancellationToken ct)
    {
        await forgotValidator.ValidateOrThrowAsync(req, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == User.Normalize(req.Email), ct);
        if (user is null || !user.IsActive) return;

        var token = await IssueVerificationCodeAsync(user, VerificationPurpose.PasswordReset, TimeSpan.FromMinutes(_opt.PasswordResetTokenMinutes), ct);
        audit.Add(AuditActions.PasswordResetRequested, nameof(User), user.Id.ToString(), userId: user.Id);
        await db.SaveChangesAsync(ct);

        var link = OneTimeLinks.Build(_opt.AppBaseUrl, "reset-password", token);
        await email.TrySendAsync(logger, "password-reset", new EmailMessage(user.Email, "إعادة تعيين كلمة المرور",
            EmailTemplate("إعادة تعيين كلمة المرور", user.FullName,
                $"اضغط على الرابط لتعيين كلمة مرور جديدة. الرابط صالح لمدة {_opt.PasswordResetTokenMinutes} دقيقة ولمرة واحدة.", link, "تعيين كلمة مرور جديدة")), ct);
    }

    public Task ResetPasswordAsync(ResetPasswordRequest req, CancellationToken ct) =>
        SetPasswordFromTokenAsync(req, VerificationPurpose.PasswordReset, ct);

    /// <summary>
    /// Accepts a staff invitation and activates the seat it was issued for (T-010). The link proves email ownership,
    /// so it also confirms the email.
    /// - New account (no password yet): anonymous; <c>newPassword</c> is required and becomes the password.
    /// - Existing account: the caller must be signed in as that account (any session); the password is never changed
    ///   here, so a leaked link can't take over an account. Otherwise 409 INVITATION_SIGN_IN_REQUIRED.
    /// A link for a seat that is no longer pending (accepted, revoked by disabling) is invalid.
    /// </summary>
    public async Task AcceptInvitationAsync(AcceptInvitationRequest req, CancellationToken ct)
    {
        await acceptValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var code = await FindUsableCodeAsync(req.Token, VerificationPurpose.Invitation, ct);
        var user = await db.Users.FirstAsync(u => u.Id == code.UserId, ct);
        var isNewAccount = string.IsNullOrEmpty(user.PasswordHash);

        // Links issued before T-010 carry no seat id: their seat was created active, only the password is missing.
        TenantMembership? seat = null;
        if (InvitationTokens.MembershipIdOf(req.Token) is { } membershipId)
        {
            seat = await db.TenantMemberships.IgnoreQueryFilters(QueryFilters.TenantOnly).Include(m => m.Tenant)
                       .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == user.Id && m.Status == MembershipStatus.Invited, ct)
                   ?? throw new AppException(ErrorCodes.InvalidOrExpiredToken, 400);
            // GD-04: a suspended or closed clinic takes no new members, the same rule sign-in applies. Checked before
            // anything changes, so the link still works once the clinic is reactivated (until it expires).
            EnsureTenantUsable(seat.Tenant);
        }
        else if (!isNewAccount)
        {
            throw new AppException(ErrorCodes.InvalidOrExpiredToken, 400);
        }

        if (isNewAccount)
        {
            if (req.NewPassword is null)
                throw new RequestValidationException(new Dictionary<string, string[]> { ["newPassword"] = [ValidationCodes.Required] });
            user.ChangePasswordHash(hasher.Hash(user, req.NewPassword));
            await RevokeUserSessionsAsync(user.Id, "invitation", now, ct);
        }
        else if (currentUser.UserId != user.Id)
        {
            throw new ConflictException(ErrorCodes.InvitationSignInRequired);
        }

        if (string.Equals(user.Email, code.Target, StringComparison.OrdinalIgnoreCase)) user.EmailConfirmed = true;
        code.ConsumedAtUtc = now;
        audit.Add(AuditActions.InvitationAccepted, nameof(User), user.Id.ToString(), tenantId: seat?.TenantId, userId: user.Id,
            details: new { via = VerificationPurpose.Invitation.ToString(), newAccount = isNewAccount });

        if (seat is null)
        {
            await db.SaveChangesAsync(ct);
            return;
        }
        seat.Status = MembershipStatus.Active;
        seat.UpdatedAtUtc = now;
        // The seat belongs to the inviting clinic, not to the caller's current session: scope the write to it.
        using (tenantContext.UseTenant(seat.TenantId))
            await db.SaveChangesAsync(ct);
    }

    private async Task SetPasswordFromTokenAsync(ResetPasswordRequest req, VerificationPurpose purpose, CancellationToken ct)
    {
        await resetValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var code = await FindUsableCodeAsync(req.Token, purpose, ct);
        var user = await db.Users.FirstAsync(u => u.Id == code.UserId, ct);

        user.ChangePasswordHash(hasher.Hash(user, req.NewPassword));
        if (purpose == VerificationPurpose.Invitation && string.Equals(user.Email, code.Target, StringComparison.OrdinalIgnoreCase))
            user.EmailConfirmed = true;
        code.ConsumedAtUtc = now;
        // Setting a password from an emailed link ends every existing session.
        await RevokeUserSessionsAsync(user.Id, purpose == VerificationPurpose.Invitation ? "invitation" : "password_reset", now, ct);
        audit.Add(purpose == VerificationPurpose.Invitation ? AuditActions.InvitationAccepted : AuditActions.PasswordChanged,
            nameof(User), user.Id.ToString(), userId: user.Id, details: new { via = purpose.ToString() });
        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- signed-in account operations

    /// <summary>Changes the password and ends every session; the caller gets a fresh one.</summary>
    public async Task<AuthResult> ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct)
    {
        await changePasswordValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var user = await CurrentUserEntityAsync(ct);
        if (!hasher.Verify(user, user.PasswordHash, req.CurrentPassword))
        {
            user.RegisterFailedLogin(now, _opt.MaxFailedAccessAttempts, TimeSpan.FromMinutes(_opt.LockoutMinutes));
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials);
        }

        user.ChangePasswordHash(hasher.Hash(user, req.NewPassword));
        await RevokeUserSessionsAsync(user.Id, "password_changed", now, ct);
        audit.Add(AuditActions.PasswordChanged, nameof(User), user.Id.ToString(), userId: user.Id, details: new { via = "account" });
        await db.SaveChangesAsync(ct);

        var membership = currentUser.TenantId is { } t ? (await LoadActiveMembershipsAsync(user.Id, t, ct)).SingleOrDefault() : null;
        return await IssueSessionAsync(user, membership, membership?.Tenant, familyId: null, ct);
    }

    /// <summary>Moves the current device to another clinic the user belongs to (new session, old one revoked).</summary>
    public async Task<AuthResult> SwitchTenantAsync(Guid tenantId, CancellationToken ct)
    {
        var user = await CurrentUserEntityAsync(ct);
        var membership = (await LoadActiveMembershipsAsync(user.Id, tenantId, ct)).SingleOrDefault()
                         ?? throw new ForbiddenException(ErrorCodes.NoActiveMembership);
        EnsureTenantUsable(membership.Tenant);

        if (currentUser.SessionId is { } sid) await RevokeFamilyAsync(sid, "tenant_switched", clock.UtcNow, ct);
        audit.Add(AuditActions.TenantSwitched, nameof(Tenant), tenantId.ToString(), tenantId: tenantId, userId: user.Id);
        await db.SaveChangesAsync(ct);
        return await IssueSessionAsync(user, membership, membership.Tenant, familyId: null, ct);
    }

    public async Task<IReadOnlyList<TenantSummary>> ListMyTenantsAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException();
        return (await LoadActiveMembershipsAsync(userId, null, ct))
            .Select(m => new TenantSummary(m.TenantId, m.Tenant!.Name, m.Tenant.Slug)).ToList();
    }

    /// <summary>Active sign-ins (one per refresh-token family), newest first.</summary>
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException();
        var now = clock.UtcNow;
        var active = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null && t.ExpiresAtUtc > now)
            .OrderByDescending(t => t.CreatedAtUtc)
            .Take(50)
            .ToListAsync(ct);
        return active.GroupBy(t => t.FamilyId).Select(g => g.First())
            .Select(t => new SessionInfo(t.FamilyId, t.TenantId, t.CreatedAtUtc, t.CreatedByIp, t.UserAgent, t.FamilyId == currentUser.SessionId))
            .ToList();
    }

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException();
        // Scoped to the caller's own tokens: another user's session id is simply "not found".
        if (!await db.RefreshTokens.AnyAsync(t => t.FamilyId == sessionId && t.UserId == userId, ct))
            throw new NotFoundException(ErrorCodes.SessionNotFound);
        await RevokeFamilyAsync(sessionId, "user_revoked", clock.UtcNow, ct);
        audit.Add(AuditActions.SessionRevoked, nameof(RefreshToken), sessionId.ToString(), userId: userId);
        await db.SaveChangesAsync(ct);
    }

    private async Task<User> CurrentUserEntityAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive) throw new UnauthorizedException();
        return user;
    }

    internal Task RevokeUserSessionsAsync(Guid userId, string reason, DateTime now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ForEachAsync(t => t.Revoke(now, reason), ct);

    public async Task VerifyEmailAsync(VerifyEmailRequest req, CancellationToken ct)
    {
        await verifyValidator.ValidateOrThrowAsync(req, ct);
        var code = await FindUsableCodeAsync(req.Token, VerificationPurpose.EmailVerification, ct);
        var user = await db.Users.FirstAsync(u => u.Id == code.UserId, ct);
        if (!string.Equals(user.Email, code.Target, StringComparison.OrdinalIgnoreCase))
            throw new AppException(ErrorCodes.InvalidOrExpiredToken, 400);

        user.EmailConfirmed = true;
        code.ConsumedAtUtc = clock.UtcNow;
        audit.Add(AuditActions.EmailVerified, nameof(User), user.Id.ToString(), userId: user.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task ResendVerificationAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new UnauthorizedException();
        if (user.EmailConfirmed) return;
        var token = await IssueVerificationCodeAsync(user, VerificationPurpose.EmailVerification, TimeSpan.FromHours(_opt.EmailVerificationTokenHours), ct);
        await db.SaveChangesAsync(ct);
        await SendVerificationEmailAsync(user, token, ct);
    }

    // ---------------------------------------------------------------- helpers

    private static readonly User DummyUser = new() { Email = "x", NormalizedEmail = "X", FullName = "x" };
    private static string? _dummyHash;

    private static void EnsureTenantUsable(Tenant? tenant)
    {
        if (tenant is { Status: TenantStatus.Suspended or TenantStatus.Closed })
            throw new ForbiddenException(ErrorCodes.TenantSuspended);
    }

    /// <summary>Memberships across tenants for one user — the one place auth looks past the tenant filter.</summary>
    private Task<List<TenantMembership>> LoadActiveMembershipsAsync(Guid userId, Guid? tenantId, CancellationToken ct) =>
        db.TenantMemberships
            .IgnoreQueryFilters(QueryFilters.TenantOnly)
            .Include(m => m.Tenant)
            .Include(m => m.Branches)
            .Include(m => m.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active)
            .Where(m => tenantId == null || m.TenantId == tenantId)
            .ToListAsync(ct);

    private async Task<AuthResult> IssueSessionAsync(
        User user, TenantMembership? membership, Tenant? tenant, Guid? familyId, CancellationToken ct, RefreshToken? rotateFrom = null)
    {
        var now = clock.UtcNow;
        // One plane per token: a clinic session carries only that membership's clinic permissions, a platform
        // session (no tenant) only the platform role's permissions. Never both (QA-03). Platform.* can't be held by
        // tenant roles, but filter anyway so a bad role row can never smuggle one into a clinic token.
        var permissions = membership is null
            ? new HashSet<string>(SystemRoles.PlatformPermissions(user.PlatformRole))
            : membership.Roles.SelectMany(r => r.Role?.Permissions ?? []).Select(p => p.Permission)
                .Where(Permissions.IsTenantPermission).ToHashSet();

        var sessionId = familyId ?? Guid.NewGuid();
        var access = tokens.CreateAccessToken(new TokenClaims(
            user.Id, user.Email, user.FullName, membership?.TenantId, membership?.Id, user.PlatformRole,
            permissions, membership?.Branches.Select(b => b.BranchId).ToList() ?? [], sessionId));

        var rawRefresh = tokens.CreateOpaqueToken();
        var refresh = new RefreshToken
        {
            UserId = user.Id,
            TenantId = membership?.TenantId,
            TokenHash = tokens.Hash(rawRefresh),
            FamilyId = sessionId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_opt.RefreshTokenDays),
            CreatedByIp = request.IpAddress,
            UserAgent = Truncate(request.UserAgent, 256),
        };
        db.RefreshTokens.Add(refresh);
        rotateFrom?.Revoke(now, "rotated", refresh.Id);
        await db.SaveChangesAsync(ct);

        return new AuthResult(
            access.Token, access.ExpiresAtUtc, rawRefresh, refresh.ExpiresAtUtc,
            new UserSummary(user.Id, user.FullName, user.Email, user.EmailConfirmed, user.PlatformRole.ToString(), user.PreferredLocale),
            tenant is null ? null : new TenantSummary(tenant.Id, tenant.Name, tenant.Slug),
            permissions.Order().ToList());
    }

    private Task RevokeFamilyAsync(Guid familyId, string reason, DateTime now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAtUtc == null)
            .ForEachAsync(t => t.Revoke(now, reason), ct);

    private async Task<string> IssueVerificationCodeAsync(User user, VerificationPurpose purpose, TimeSpan lifetime, CancellationToken ct)
    {
        var now = clock.UtcNow;
        // Only the newest code of a purpose is valid.
        await db.VerificationCodes
            .Where(c => c.UserId == user.Id && c.Purpose == purpose && c.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAtUtc, now), ct);

        var raw = tokens.CreateOpaqueToken();
        db.VerificationCodes.Add(new VerificationCode
        {
            UserId = user.Id,
            Purpose = purpose,
            Target = user.Email,
            CodeHash = tokens.Hash(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.Add(lifetime),
            CreatedByIp = request.IpAddress,
        });
        return raw;
    }

    private async Task<VerificationCode> FindUsableCodeAsync(string rawToken, VerificationPurpose purpose, CancellationToken ct)
    {
        var hash = tokens.Hash(rawToken);
        var code = await db.VerificationCodes.FirstOrDefaultAsync(c => c.CodeHash == hash && c.Purpose == purpose, ct);
        if (code is null || !code.IsUsable(clock.UtcNow)) throw new AppException(ErrorCodes.InvalidOrExpiredToken, 400);
        return code;
    }

    private Task SendVerificationEmailAsync(User user, string token, CancellationToken ct)
    {
        var link = OneTimeLinks.Build(_opt.AppBaseUrl, "verify-email", token);
        return email.TrySendAsync(logger, "email-verification", new EmailMessage(user.Email, "تأكيد البريد الإلكتروني",
            EmailTemplate("تأكيد البريد الإلكتروني", user.FullName, "مرحباً بك! يرجى تأكيد بريدك الإلكتروني لتفعيل حسابك.", link, "تأكيد البريد")), ct);
    }

    /// <summary>Minimal RTL email. User-supplied values are HTML-encoded.</summary>
    private static string EmailTemplate(string title, string name, string body, string link, string cta) =>
        $"""
        <div dir="rtl" style="font-family:'IBM Plex Sans Arabic',Tahoma,sans-serif;max-width:520px;color:#0f172a">
          <h2 style="margin:0 0 8px">{WebUtility.HtmlEncode(title)}</h2>
          <p>مرحباً {WebUtility.HtmlEncode(name)}،</p>
          <p>{WebUtility.HtmlEncode(body)}</p>
          <p><a href="{WebUtility.HtmlEncode(link)}" style="background:#0d9488;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none">{WebUtility.HtmlEncode(cta)}</a></p>
          <p style="color:#64748b;font-size:12px">إذا لم تطلب ذلك، تجاهل هذه الرسالة.</p>
        </div>
        """;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
