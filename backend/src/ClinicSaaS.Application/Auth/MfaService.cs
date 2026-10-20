using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Domain.Identity;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClinicSaaS.Application.Auth;

/// <summary>
/// Second factor (TOTP + one-time recovery codes) for platform accounts (<c>PlatformRole != None</c>).
///
/// Before MFA succeeds there is no session at all — no access token, no refresh token, no cookie, no session family.
/// The in-between states are stateless Data-Protection tokens (see <see cref="IMfaProtector"/>). Every attempt that
/// involves a code first claims one attempt from the account's failure counter in a single atomic UPDATE, so parallel
/// guesses cannot exceed the lockout threshold; only a success resets the counter (never the password step).
/// </summary>
public sealed class MfaService(
    IAppDbContext db,
    IPasswordHasher hasher,
    ITokenService tokens,
    IClock clock,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IMfaProtector protector,
    AuthService auth,
    IOptions<AuthOptions> options,
    IValidator<EnrollStartRequest> enrollStartValidator,
    IValidator<EnrollCompleteRequest> enrollCompleteValidator,
    IValidator<MfaVerifyRequest> verifyValidator,
    IValidator<RegenerateRecoveryCodesRequest> regenerateValidator,
    IValidator<RemoveMfaRequest> removeValidator,
    ILogger<MfaService> logger)
{
    public const string Issuer = "ClinicSaaS";
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(10);

    private readonly AuthOptions _opt = options.Value;

    private enum CodeResult { Accepted, Wrong, Replay }

    // ---------------------------------------------------------------- enrolment

    public async Task<EnrollStartResult> StartEnrollmentAsync(EnrollStartRequest req, CancellationToken ct)
    {
        await enrollStartValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var token = protector.TryUnprotectToken(MfaTokenPurpose.EnrollmentStart, req.EnrollmentToken, now)
                    ?? throw new UnauthorizedException(ErrorCodes.InvalidMfaToken);
        var user = await LoadPlatformUserAsync(token.UserId, ct);
        if (user.IsLockedOut(now)) throw new LockedException(ErrorCodes.AccountLocked);
        if (await HasFactorAsync(user.Id, ct)) throw new ConflictException(ErrorCodes.MfaAlreadyEnrolled);

        var secret = Totp.ToBase32(Totp.GenerateSecret());
        var expires = now.Add(EnrollmentLifetime);
        var pending = protector.ProtectToken(MfaTokenPurpose.EnrollmentPending, new MfaTokenPayload(user.Id, token.TenantId, secret), expires);

        audit.Add(AuditActions.MfaEnrollmentStarted, nameof(User), user.Id.ToString(), userId: user.Id);
        await db.SaveChangesAsync(ct);
        return new EnrollStartResult(secret, Totp.SetupUri(Issuer, user.Email, secret), pending, expires);
    }

    public async Task<EnrollCompleteResult> CompleteEnrollmentAsync(EnrollCompleteRequest req, CancellationToken ct)
    {
        await enrollCompleteValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var token = protector.TryUnprotectToken(MfaTokenPurpose.EnrollmentPending, req.PendingToken, now)
                    ?? throw new UnauthorizedException(ErrorCodes.InvalidMfaToken);
        var user = await LoadPlatformUserAsync(token.UserId, ct);
        var secret = token.Data is null ? null : Totp.FromBase32(token.Data);
        if (secret is not { Length: Totp.SecretBytes }) throw new UnauthorizedException(ErrorCodes.InvalidMfaToken);
        if (await HasFactorAsync(user.Id, ct)) throw new ConflictException(ErrorCodes.MfaAlreadyEnrolled);

        // The user must prove the authenticator produces codes for THIS secret before anything is stored.
        if (!Totp.TryMatch(secret, Totp.NormalizeCode(req.Code), now, lastUsedStep: null, out var step))
        {
            audit.Add(AuditActions.MfaEnrollmentFailed, nameof(User), user.Id.ToString(), AuditResult.Failure, userId: user.Id);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidMfaCode);
        }

        var recoveryCodes = RecoveryCodes.Generate();
        try
        {
            await db.InTransactionAsync(async c =>
            {
                db.UserTotpFactors.Add(new UserTotpFactor
                {
                    UserId = user.Id,
                    SecretProtected = protector.ProtectSecret(user.Id, secret),
                    LastUsedTimeStep = step, // the code that proved possession cannot be replayed to sign in
                    CreatedAtUtc = now,
                });
                AddRecoveryCodes(user.Id, recoveryCodes, now);
                // Anything signed in before this moment is not MFA-derived: end it all, then create the one real session.
                await auth.RevokeUserSessionsAsync(user.Id, "mfa_enrolled", now, c);
                audit.Add(AuditActions.MfaEnabled, nameof(User), user.Id.ToString(), userId: user.Id);
                await db.SaveChangesAsync(c);
                return true;
            }, ct: ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race against a concurrent enrolment (unique index on UserId): exactly one winner.
            if (await HasFactorAsync(user.Id, ct)) throw new ConflictException(ErrorCodes.MfaAlreadyEnrolled);
            throw;
        }

        await ResetCounterAsync(user.Id, now, recordLogin: false, ct);
        var session = await auth.CompleteMfaLoginAsync(user, token.TenantId, ct);
        return new EnrollCompleteResult(session, recoveryCodes);
    }

    // ---------------------------------------------------------------- sign-in challenge

    public async Task<AuthResult> VerifyAsync(MfaVerifyRequest req, CancellationToken ct)
    {
        await verifyValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var token = protector.TryUnprotectToken(MfaTokenPurpose.Challenge, req.ChallengeToken, now)
                    ?? throw new UnauthorizedException(ErrorCodes.InvalidMfaToken);
        var user = await LoadPlatformUserAsync(token.UserId, ct);
        var useRecovery = !string.IsNullOrWhiteSpace(req.RecoveryCode);

        await ReserveAttemptAsync(user, now, ct);
        var recoveryHash = useRecovery ? HashRecoveryCode(req.RecoveryCode) : null;
        var result = useRecovery
            ? await TryConsumeRecoveryCodeAsync(user.Id, recoveryHash, now, ct)
            : await TryConsumeTotpAsync(user.Id, req.TotpCode!, now, ct);
        if (result != CodeResult.Accepted) await FailAsync(user, useRecovery ? "recovery" : "totp", result, now, ct);

        await ResetCounterAsync(user.Id, now, recordLogin: true, ct);
        audit.Add(AuditActions.MfaChallengeSucceeded, nameof(User), user.Id.ToString(),
            details: new { kind = useRecovery ? "recovery" : "totp" }, userId: user.Id);
        if (useRecovery)
        {
            var remaining = await db.UserRecoveryCodes.CountAsync(c => c.UserId == user.Id && c.ConsumedAtUtc == null, ct);
            audit.Add(AuditActions.MfaRecoveryCodeUsed, nameof(User), user.Id.ToString(), details: new { remaining }, userId: user.Id);
        }
        return await auth.CompleteMfaLoginAsync(user, token.TenantId, ct);
    }

    // ---------------------------------------------------------------- signed-in operations

    /// <summary>Replaces all recovery codes. Needs a current TOTP — a recovery code cannot authorise this.</summary>
    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(RegenerateRecoveryCodesRequest req, CancellationToken ct)
    {
        await regenerateValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var user = await LoadSessionPlatformUserAsync(ct);

        await ReserveAttemptAsync(user, now, ct);
        var result = await TryConsumeTotpAsync(user.Id, req.TotpCode, now, ct);
        if (result != CodeResult.Accepted) await FailAsync(user, "totp", result, now, ct);

        var codes = RecoveryCodes.Generate();
        await db.InTransactionAsync(async c =>
        {
            await db.UserRecoveryCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(c);
            AddRecoveryCodes(user.Id, codes, now);
            audit.Add(AuditActions.MfaRecoveryCodesRegenerated, nameof(User), user.Id.ToString(), userId: user.Id);
            await db.SaveChangesAsync(c);
            return true;
        }, ct: ct);
        await ResetCounterAsync(user.Id, now, recordLogin: false, ct);
        return codes;
    }

    /// <summary>
    /// Removes the caller's own authenticator and ends EVERY session including this one; no new session is created, so
    /// the next sign-in must enrol again. Needs the current password and a TOTP or recovery code.
    /// </summary>
    public async Task RemoveAsync(RemoveMfaRequest req, CancellationToken ct)
    {
        await removeValidator.ValidateOrThrowAsync(req, ct);
        var now = clock.UtcNow;
        var user = await LoadSessionPlatformUserAsync(ct);
        var useRecovery = !string.IsNullOrWhiteSpace(req.RecoveryCode);

        await ReserveAttemptAsync(user, now, ct);
        if (!hasher.Verify(user, user.PasswordHash, req.CurrentPassword))
        {
            audit.Add(AuditActions.MfaChallengeFailed, nameof(User), user.Id.ToString(), AuditResult.Failure,
                details: new { kind = "remove", reason = "wrong_password" }, userId: user.Id);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedException(ErrorCodes.InvalidCredentials);
        }

        var recoveryHash = useRecovery ? HashRecoveryCode(req.RecoveryCode) : null;
        var result = useRecovery
            ? await TryConsumeRecoveryCodeAsync(user.Id, recoveryHash, now, ct)
            : await TryConsumeTotpAsync(user.Id, req.TotpCode!, now, ct);
        if (result != CodeResult.Accepted) await FailAsync(user, useRecovery ? "recovery" : "totp", result, now, ct);

        await db.InTransactionAsync(async c =>
        {
            await db.UserTotpFactors.Where(f => f.UserId == user.Id).ExecuteDeleteAsync(c);
            await db.UserRecoveryCodes.Where(x => x.UserId == user.Id).ExecuteDeleteAsync(c);
            await auth.RevokeUserSessionsAsync(user.Id, "mfa_removed", now, c);
            audit.Add(AuditActions.MfaDisabled, nameof(User), user.Id.ToString(), userId: user.Id);
            await db.SaveChangesAsync(c);
            return true;
        }, ct: ct);
        await ResetCounterAsync(user.Id, now, recordLogin: false, ct);
    }

    // ---------------------------------------------------------------- attempts and lockout

    /// <summary>
    /// Claims one attempt, atomically: a single UPDATE that only applies to an account that is not locked, counts the
    /// attempt, and starts the (MFA) lockout when the threshold is reached. No read-modify-write, so N parallel
    /// guesses can never be granted more than the threshold. Zero rows ⇒ the account is locked.
    /// </summary>
    private async Task ReserveAttemptAsync(User user, DateTime now, CancellationToken ct)
    {
        var max = _opt.MaxFailedAccessAttempts;
        var lockoutEnd = (DateTime?)now.AddMinutes(_opt.MfaLockoutMinutes);
        var claimed = await db.Users
            .Where(u => u.Id == user.Id && (u.LockoutEndUtc == null || u.LockoutEndUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LockoutEndUtc, u => u.AccessFailedCount + 1 >= max ? lockoutEnd : u.LockoutEndUtc)
                .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1 >= max ? 0 : u.AccessFailedCount + 1), ct);
        if (claimed != 1) throw new LockedException(ErrorCodes.AccountLocked);
    }

    /// <summary>Success: the attempt that was claimed up front is forgiven and any lockout cleared.</summary>
    private async Task ResetCounterAsync(Guid userId, DateTime now, bool recordLogin, CancellationToken ct)
    {
        if (recordLogin)
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AccessFailedCount, 0).SetProperty(u => u.LockoutEndUtc, (DateTime?)null).SetProperty(u => u.LastLoginAtUtc, (DateTime?)now), ct);
        else
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.AccessFailedCount, 0).SetProperty(u => u.LockoutEndUtc, (DateTime?)null), ct);
    }

    /// <summary>Records the failure (the counter was already charged) and answers with the one uniform error.</summary>
    private async Task FailAsync(User user, string kind, CodeResult result, DateTime now, CancellationToken ct)
    {
        audit.Add(AuditActions.MfaChallengeFailed, nameof(User), user.Id.ToString(), AuditResult.Failure,
            details: new { kind, reason = result == CodeResult.Replay ? "replay" : "wrong_code" }, userId: user.Id);
        var lockedUntil = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => u.LockoutEndUtc).FirstOrDefaultAsync(ct);
        if (lockedUntil is { } end && end > now)
            audit.Add(AuditActions.AccountLocked, nameof(User), user.Id.ToString(), AuditResult.Failure, details: new { via = "mfa" }, userId: user.Id);
        await db.SaveChangesAsync(ct);
        throw new UnauthorizedException(ErrorCodes.InvalidMfaCode);
    }

    // ---------------------------------------------------------------- codes

    private async Task<CodeResult> TryConsumeTotpAsync(Guid userId, string rawCode, DateTime now, CancellationToken ct)
    {
        var factor = await db.UserTotpFactors.AsNoTracking().FirstOrDefaultAsync(f => f.UserId == userId, ct);
        if (factor is null) return CodeResult.Wrong;

        var secret = protector.TryUnprotectSecret(userId, factor.SecretProtected);
        if (secret is null)
        {
            // Key ring lost or rotated away: fail closed, loudly (the message carries ids only, never a secret).
            logger.LogError(SecurityLogEvents.MfaKeyRingUnreadable, "The MFA secret of user {UserId} could not be decrypted (is the Data Protection key ring available?)", userId);
            return CodeResult.Wrong;
        }

        var code = Totp.NormalizeCode(rawCode);
        if (!Totp.TryMatch(secret, code, now, factor.LastUsedTimeStep, out var step))
            return Totp.TryMatch(secret, code, now, lastUsedStep: null, out _) ? CodeResult.Replay : CodeResult.Wrong;

        // Replay protection is a single conditional UPDATE: exactly one concurrent submission of a code can win.
        var won = await db.UserTotpFactors
            .Where(f => f.UserId == userId && (f.LastUsedTimeStep == null || f.LastUsedTimeStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastUsedTimeStep, (long?)step), ct);
        return won == 1 ? CodeResult.Accepted : CodeResult.Replay;
    }

    /// <summary>One-time consumption is a single conditional UPDATE: exactly one concurrent use of a code can win.</summary>
    private async Task<CodeResult> TryConsumeRecoveryCodeAsync(Guid userId, string? hash, DateTime now, CancellationToken ct)
    {
        if (hash is null) return CodeResult.Wrong;
        var consumed = await db.UserRecoveryCodes
            .Where(c => c.UserId == userId && c.CodeHash == hash && c.ConsumedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAtUtc, (DateTime?)now), ct);
        if (consumed == 1) return CodeResult.Accepted;
        var existed = await db.UserRecoveryCodes.AsNoTracking().AnyAsync(c => c.UserId == userId && c.CodeHash == hash, ct);
        return existed ? CodeResult.Replay : CodeResult.Wrong;
    }

    /// <summary>The hash to look up, or null for something that cannot be a recovery code (never reaches the database).</summary>
    private string? HashRecoveryCode(string? raw)
    {
        var normalized = RecoveryCodes.Normalize(raw);
        return RecoveryCodes.LooksValid(normalized) ? tokens.Hash(normalized) : null;
    }

    private void AddRecoveryCodes(Guid userId, IReadOnlyList<string> codes, DateTime now)
    {
        foreach (var code in codes)
            db.UserRecoveryCodes.Add(new UserRecoveryCode { UserId = userId, CodeHash = tokens.Hash(RecoveryCodes.Normalize(code)), CreatedAtUtc = now });
    }

    // ---------------------------------------------------------------- helpers

    private Task<bool> HasFactorAsync(Guid userId, CancellationToken ct) =>
        db.UserTotpFactors.AsNoTracking().AnyAsync(f => f.UserId == userId, ct);

    /// <summary>The account a challenge / enrolment token was issued for: must exist, be active and be a platform account.</summary>
    private async Task<User> LoadPlatformUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new UnauthorizedException(ErrorCodes.InvalidMfaToken);
        if (!user.IsActive) throw new ForbiddenException(ErrorCodes.AccountDisabled);
        if (user.PlatformRole == PlatformRole.None) throw new UnauthorizedException(ErrorCodes.InvalidMfaToken);
        return user;
    }

    /// <summary>The signed-in caller, who must be an active platform account.</summary>
    private async Task<User> LoadSessionPlatformUserAsync(CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new UnauthorizedException();
        if (!user.IsActive) throw new UnauthorizedException();
        if (user.PlatformRole == PlatformRole.None) throw new ForbiddenException();
        return user;
    }
}
