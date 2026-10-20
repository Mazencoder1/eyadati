using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Application.Auth;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.IntegrationTests.Infrastructure;
using ClinicSaaS.IntegrationTests.Qa;
using FluentAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;

namespace ClinicSaaS.IntegrationTests;

/// <summary>
/// Platform-admin MFA (TOTP + recovery codes) through the real API and a real database. Every platform account is
/// created without a factor and goes through the real enrolment endpoints; nothing is seeded.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PlatformMfaTests(ApiFactory factory)
{
    private const string LoginUrl = "/api/v1/auth/login";
    private const string Mfa = "/api/v1/auth/mfa";
    private const string NewPassword = "Another-Str0ng-Passw0rd!";
    private const string Wrong = "999999";

    private sealed record Enrolled(string Email, Guid UserId, byte[] Secret, string SetupSecret, IReadOnlyList<string> RecoveryCodes,
        HttpClient Client, JsonElement Session);

    // ------------------------------------------------------------------ helpers

    private async Task<(string Email, Guid UserId)> NewAdminAsync(PlatformRole role = PlatformRole.SuperAdmin)
    {
        var email = await factory.CreatePlatformUserAsync(role);
        return (email, await UserIdAsync(email));
    }

    private async Task<Guid> UserIdAsync(string email)
    {
        var id = Guid.Empty;
        await factory.WithDbAsync(null, async db => id = await db.Users.Where(u => u.NormalizedEmail == User.Normalize(email)).Select(u => u.Id).SingleAsync());
        return id;
    }

    private HttpClient Client(bool native = true)
    {
        var client = factory.CreateClient();
        if (native) client.DefaultRequestHeaders.Add("X-Client-Type", "mobile"); // refresh token in the body, easy to inspect
        return client;
    }

    private static Task<HttpResponseMessage> PasswordLoginAsync(HttpClient client, string email, string password = TestApi.Password, Guid? tenantId = null) =>
        client.PostAsJsonAsync(LoginUrl, new { email, password, tenantId });

    private static async Task<JsonElement> DetailsAsync(HttpResponseMessage res, HttpStatusCode status, string code)
    {
        res.StatusCode.Should().Be(status, await res.Content.ReadAsStringAsync());
        var body = await res.ReadJsonAsync();
        body.GetProperty("code").GetString().Should().Be(code);
        return body.TryGetProperty("details", out var d) ? d : default;
    }

    /// <summary>Password-only sign-in of an account that has an authenticator → the challenge token.</summary>
    private async Task<string> ChallengeAsync(string email, HttpClient? client = null, Guid? tenantId = null) =>
        (await DetailsAsync(await PasswordLoginAsync(client ?? Client(), email, tenantId: tenantId), HttpStatusCode.Conflict, "MFA_REQUIRED"))
        .GetProperty("challengeToken").GetString()!;

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, string challengeToken, string? totp = null, string? recovery = null) =>
        client.PostAsJsonAsync($"{Mfa}/verify", new { challengeToken, totpCode = totp, recoveryCode = recovery });

    /// <summary>A code the server accepts even if the same authenticator signed in a moment ago (fixture-only reset).</summary>
    private async Task<string> FreshCodeAsync(Enrolled a)
    {
        await factory.ForgetLastTimeStepAsync(a.Email);
        return TestApi.CurrentCode(a.Secret);
    }

    private async Task<Enrolled> EnrollAsync(string? email = null, bool native = true)
    {
        Guid userId;
        if (email is null) (email, userId) = await NewAdminAsync();
        else userId = await UserIdAsync(email);

        var client = Client(native);
        var step = await DetailsAsync(await PasswordLoginAsync(client, email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");
        var start = await client.PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = step.GetProperty("enrollmentToken").GetString() });
        var setup = await start.DataAsync(200);
        var setupSecret = setup.GetProperty("secret").GetString()!;
        var secret = Totp.FromBase32(setupSecret)!;
        var done = await client.PostAsJsonAsync($"{Mfa}/enroll/complete",
            new { pendingToken = setup.GetProperty("pendingToken").GetString(), code = TestApi.CurrentCode(secret) });
        var data = await done.DataAsync(200);
        var session = data.GetProperty("session");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        factory.MfaSecrets[email] = secret;
        return new Enrolled(email, userId, secret, setupSecret,
            data.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList(), client, session);
    }

    private async Task<(int Failed, DateTime? LockedUntil, DateTime? LastLogin, bool Active)> StateAsync(Guid userId)
    {
        (int, DateTime?, DateTime?, bool) state = default;
        await factory.WithDbAsync(null, async db =>
        {
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == userId);
            state = (u.AccessFailedCount, u.LockoutEndUtc, u.LastLoginAtUtc, u.IsActive);
        });
        return state;
    }

    private async Task<int> FactorCountAsync(Guid userId)
    {
        var n = 0;
        await factory.WithDbAsync(null, async db => n = await db.UserTotpFactors.CountAsync(f => f.UserId == userId));
        return n;
    }

    private async Task<(int Total, int Unconsumed)> RecoveryCountsAsync(Guid userId)
    {
        (int, int) n = default;
        await factory.WithDbAsync(null, async db =>
            n = (await db.UserRecoveryCodes.CountAsync(c => c.UserId == userId), await db.UserRecoveryCodes.CountAsync(c => c.UserId == userId && c.ConsumedAtUtc == null)));
        return n;
    }

    private async Task<int> SessionRowsAsync(Guid userId)
    {
        var n = 0;
        await factory.WithDbAsync(null, async db => n = await db.RefreshTokens.CountAsync(t => t.UserId == userId));
        return n;
    }

    private async Task<List<AuditLog>> AuditAsync(Guid userId)
    {
        List<AuditLog> rows = [];
        await factory.WithDbAsync(null, async db => rows = await db.AuditLogs.AsNoTracking().Where(a => a.UserId == userId).OrderBy(a => a.Id).ToListAsync());
        return rows;
    }

    private static async Task ShouldBeEndedAsync(HttpResponseMessage res) => res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, await res.Content.ReadAsStringAsync());

    private static bool HasCookie(HttpResponseMessage res) => res.Headers.TryGetValues("Set-Cookie", out var v) && v.Any(c => c.StartsWith("rt=", StringComparison.Ordinal) && !c.Contains("expires=Thu, 01 Jan 1970"));

    // ------------------------------------------------------------------ login: no session before MFA

    [Fact]
    public async Task A_normal_clinic_user_signs_in_exactly_as_before()
    {
        var owner = await factory.RegisterClinicAsync();
        var res = await PasswordLoginAsync(factory.CreateClient(), owner.Email);
        var data = await res.DataAsync(200);
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        HasCookie(res).Should().BeTrue();
    }

    [Fact]
    public async Task A_platform_admin_without_MFA_gets_an_enrolment_challenge_and_nothing_else()
    {
        var (email, userId) = await NewAdminAsync();
        var res = await PasswordLoginAsync(Client(native: false), email);
        var details = await DetailsAsync(res, HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");

        var text = await res.Content.ReadAsStringAsync();
        text.Should().NotContain("accessToken").And.NotContain("refreshToken");
        HasCookie(res).Should().BeFalse("no refresh cookie before MFA");
        details.GetProperty("enrollmentToken").GetString().Should().NotBeNullOrEmpty();
        details.GetProperty("expiresAtUtc").GetDateTime().Should().BeBefore(DateTime.UtcNow.AddMinutes(10).AddSeconds(5)).And.BeAfter(DateTime.UtcNow);
        (await SessionRowsAsync(userId)).Should().Be(0, "no session family exists before MFA");
    }

    [Fact]
    public async Task A_platform_admin_with_MFA_gets_a_challenge_not_a_session()
    {
        var a = await EnrollAsync();
        var before = await SessionRowsAsync(a.UserId);

        var res = await PasswordLoginAsync(Client(native: false), a.Email);
        var details = await DetailsAsync(res, HttpStatusCode.Conflict, "MFA_REQUIRED");
        (await res.Content.ReadAsStringAsync()).Should().NotContain("accessToken").And.NotContain("refreshToken");
        HasCookie(res).Should().BeFalse();
        details.GetProperty("expiresAtUtc").GetDateTime().Should().BeBefore(DateTime.UtcNow.AddMinutes(10));
        (await SessionRowsAsync(a.UserId)).Should().Be(before, "the password alone created no session family");
    }

    [Fact]
    public async Task A_platform_admin_who_names_a_clinic_is_challenged_too_and_the_clinic_is_applied_after_MFA()
    {
        var clinic = await factory.RegisterClinicAsync();
        var a = await EnrollAsync();
        await factory.AddExistingAccountAsync(clinic.Client, a.Email, await clinic.Client.RoleIdAsync("receptionist"));

        var token = await ChallengeAsync(a.Email, tenantId: clinic.TenantId);
        var session = await (await VerifyAsync(Client(), token, await FreshCodeAsync(a))).DataAsync(200);
        session.GetProperty("tenant").GetProperty("id").GetGuid().Should().Be(clinic.TenantId);
    }

    // ------------------------------------------------------------------ enrolment

    [Fact]
    public async Task Enrolment_start_shows_the_setup_secret_once_with_no_store_and_creates_nothing()
    {
        var (email, userId) = await NewAdminAsync();
        var client = Client(native: false);
        var step = await DetailsAsync(await PasswordLoginAsync(client, email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");

        var start = await client.PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = step.GetProperty("enrollmentToken").GetString() });
        start.Headers.CacheControl!.NoStore.Should().BeTrue();
        HasCookie(start).Should().BeFalse();
        var setup = await start.DataAsync(200);
        setup.GetProperty("secret").GetString().Should().MatchRegex("^[A-Z2-7]{32}$");
        setup.GetProperty("setupUri").GetString().Should().StartWith("otpauth://totp/ClinicSaaS:").And.Contain(setup.GetProperty("secret").GetString()!);
        setup.GetProperty("expiresAtUtc").GetDateTime().Should().BeBefore(DateTime.UtcNow.AddMinutes(10).AddSeconds(5));

        // Abandoned: nothing was written, so no factor, no codes, no session.
        (await FactorCountAsync(userId)).Should().Be(0);
        (await RecoveryCountsAsync(userId)).Total.Should().Be(0);
        (await SessionRowsAsync(userId)).Should().Be(0);
        (await AuditAsync(userId)).Select(r => r.Action).Should().Contain(AuditActions.MfaEnrollmentStarted);
    }

    [Fact]
    public async Task A_wrong_code_does_not_persist_a_factor_and_a_right_one_creates_the_factor_ten_hashed_codes_and_the_first_session()
    {
        var (email, userId) = await NewAdminAsync();
        var client = Client();
        var step = await DetailsAsync(await PasswordLoginAsync(client, email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");
        var setup = await (await client.PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = step.GetProperty("enrollmentToken").GetString() })).DataAsync(200);
        var pending = setup.GetProperty("pendingToken").GetString();
        var secret = Totp.FromBase32(setup.GetProperty("secret").GetString()!)!;

        var bad = await client.PostAsJsonAsync($"{Mfa}/enroll/complete", new { pendingToken = pending, code = Wrong });
        await DetailsAsync(bad, HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        (await FactorCountAsync(userId)).Should().Be(0);
        (await SessionRowsAsync(userId)).Should().Be(0, "no session without a proven authenticator");
        (await AuditAsync(userId)).Select(r => r.Action).Should().Contain(AuditActions.MfaEnrollmentFailed);

        var good = await client.PostAsJsonAsync($"{Mfa}/enroll/complete", new { pendingToken = pending, code = TestApi.CurrentCode(secret) });
        good.Headers.CacheControl!.NoStore.Should().BeTrue();
        var data = await good.DataAsync(200);
        data.GetProperty("session").GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        var codes = data.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();

        (await FactorCountAsync(userId)).Should().Be(1);
        (await RecoveryCountsAsync(userId)).Should().Be((10, 10));
        (await SessionRowsAsync(userId)).Should().BeGreaterThan(0);

        // Only hashes are stored: no recovery code (typed or normalised) appears in the table.
        await factory.WithDbAsync(null, async db =>
        {
            var hashes = await db.UserRecoveryCodes.Where(c => c.UserId == userId).Select(c => c.CodeHash).ToListAsync();
            hashes.Should().OnlyContain(h => h.Length == 64);
            foreach (var code in codes)
            {
                hashes.Should().NotContain(code).And.NotContain(RecoveryCodes.Normalize(code));
                hashes.Should().Contain(factory.Services.GetRequiredService<ITokenService>().Hash(RecoveryCodes.Normalize(code)));
            }
        });
    }

    [Fact]
    public async Task Enrolment_ends_every_session_that_existed_before()
    {
        var (email, userId) = await NewAdminAsync();
        var legacyFamily = Guid.NewGuid();
        await factory.WithDbAsync(null, async db =>
        {
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = userId, TokenHash = Convert.ToHexString(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()).ToLowerInvariant(),
                FamilyId = legacyFamily, CreatedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        });

        await EnrollAsync(email);

        await factory.WithDbAsync(null, async db =>
        {
            var legacy = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.FamilyId == legacyFamily);
            legacy.RevokedAtUtc.Should().NotBeNull();
            legacy.RevokedReason.Should().Be("mfa_enrolled");
        });
    }

    [Fact]
    public async Task A_user_who_already_has_MFA_cannot_enrol_a_second_authenticator()
    {
        var a = await EnrollAsync();
        var protector = factory.Services.GetRequiredService<IMfaProtector>();
        var forgedStep = protector.ProtectToken(MfaTokenPurpose.EnrollmentStart, new MfaTokenPayload(a.UserId, null), DateTime.UtcNow.AddMinutes(5));

        await DetailsAsync(await Client().PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = forgedStep }), HttpStatusCode.Conflict, "MFA_ALREADY_ENROLLED");

        var pending = protector.ProtectToken(MfaTokenPurpose.EnrollmentPending, new MfaTokenPayload(a.UserId, null, Totp.ToBase32(Totp.GenerateSecret())), DateTime.UtcNow.AddMinutes(5));
        await DetailsAsync(await Client().PostAsJsonAsync($"{Mfa}/enroll/complete", new { pendingToken = pending, code = Wrong }), HttpStatusCode.Conflict, "MFA_ALREADY_ENROLLED");
        (await FactorCountAsync(a.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task A_non_platform_user_cannot_start_platform_MFA_enrolment_or_answer_a_challenge()
    {
        var clinic = await factory.RegisterClinicAsync();
        var protector = factory.Services.GetRequiredService<IMfaProtector>();
        var start = protector.ProtectToken(MfaTokenPurpose.EnrollmentStart, new MfaTokenPayload(clinic.UserId, null), DateTime.UtcNow.AddMinutes(5));
        var challenge = protector.ProtectToken(MfaTokenPurpose.Challenge, new MfaTokenPayload(clinic.UserId, null), DateTime.UtcNow.AddMinutes(5));

        await DetailsAsync(await Client().PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = start }), HttpStatusCode.Unauthorized, "INVALID_MFA_TOKEN");
        await DetailsAsync(await VerifyAsync(Client(), challenge, Wrong), HttpStatusCode.Unauthorized, "INVALID_MFA_TOKEN");
        (await FactorCountAsync(clinic.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_enrolment_with_the_same_pending_token_has_exactly_one_winner()
    {
        var (email, userId) = await NewAdminAsync();
        var step = await DetailsAsync(await PasswordLoginAsync(Client(), email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");
        var setup = await (await Client().PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = step.GetProperty("enrollmentToken").GetString() })).DataAsync(200);
        var pending = setup.GetProperty("pendingToken").GetString();
        var code = TestApi.CurrentCode(Totp.FromBase32(setup.GetProperty("secret").GetString()!)!);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Client().PostAsJsonAsync($"{Mfa}/enroll/complete", new { pendingToken = pending, code })));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, string.Join(" | ", results.Select(r => (int)r.StatusCode)));
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Conflict);
        (await FactorCountAsync(userId)).Should().Be(1);
        (await RecoveryCountsAsync(userId)).Total.Should().Be(10, "the losers' recovery codes were rolled back");
    }

    // ------------------------------------------------------------------ challenge: TOTP and recovery codes

    [Fact]
    public async Task A_valid_TOTP_creates_the_session_and_resets_the_counter()
    {
        var a = await EnrollAsync();
        var web = Client(native: false);
        var token = await ChallengeAsync(a.Email, web);

        var res = await VerifyAsync(web, token, await FreshCodeAsync(a));
        var data = await res.DataAsync(200);
        HasCookie(res).Should().BeTrue();
        (await factory.WithToken(data.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StateAsync(a.UserId)).LastLogin.Should().NotBeNull();
        (await AuditAsync(a.UserId)).Select(r => r.Action).Should().Contain([AuditActions.MfaChallengeSucceeded, AuditActions.LoginSucceeded]);
    }

    [Fact]
    public async Task An_invalid_TOTP_fails_uniformly_and_creates_no_session()
    {
        var a = await EnrollAsync();
        var before = await SessionRowsAsync(a.UserId);
        var token = await ChallengeAsync(a.Email);

        var res = await VerifyAsync(Client(native: false), token, Wrong);
        await DetailsAsync(res, HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        HasCookie(res).Should().BeFalse();
        (await SessionRowsAsync(a.UserId)).Should().Be(before);
    }

    [Fact]
    public async Task A_replayed_TOTP_is_rejected_and_so_is_an_older_step()
    {
        var a = await EnrollAsync();                                    // enrolment consumed the current step t
        var next = Totp.CodeFor(a.Secret, Totp.StepOf(DateTime.UtcNow) + 1);

        await (await VerifyAsync(Client(), await ChallengeAsync(a.Email), next)).DataAsync(200);           // t+1: fine, once
        await DetailsAsync(await VerifyAsync(Client(), await ChallengeAsync(a.Email), next), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        await DetailsAsync(await VerifyAsync(Client(), await ChallengeAsync(a.Email), TestApi.CurrentCode(a.Secret)), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");

        var replay = (await AuditAsync(a.UserId)).Where(r => r.Action == AuditActions.MfaChallengeFailed).Select(r => r.Details);
        replay.Should().Contain(d => d!.Contains("replay"));
    }

    [Fact]
    public async Task A_recovery_code_works_exactly_once_and_a_reused_one_fails_like_a_wrong_one()
    {
        var a = await EnrollAsync();
        var code = a.RecoveryCodes[0];

        var first = await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: code.ToLowerInvariant().Replace("-", " ")); // typed sloppily
        await first.DataAsync(200);
        (await RecoveryCountsAsync(a.UserId)).Should().Be((10, 9));

        var again = await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: code);
        await DetailsAsync(again, HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        var junk = await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: "AAAA-AAAA-AAAA-AAAA");
        await DetailsAsync(junk, HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        (await AuditAsync(a.UserId)).Select(r => r.Action).Should().Contain(AuditActions.MfaRecoveryCodeUsed);
    }

    [Fact]
    public async Task Exactly_one_of_a_TOTP_or_a_recovery_code_must_be_sent()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);
        (await VerifyAsync(Client(), token)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VerifyAsync(Client(), token, Wrong, a.RecoveryCodes[0])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await RecoveryCountsAsync(a.UserId)).Unconsumed.Should().Be(10, "a malformed request consumes nothing");
    }

    // ------------------------------------------------------------------ concurrency

    [Fact]
    public async Task The_same_TOTP_submitted_concurrently_succeeds_once()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);
        var code = await FreshCodeAsync(a);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => VerifyAsync(Client(), token, code)));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, string.Join(" | ", results.Select(r => (int)r.StatusCode)));
        results.Where(r => r.StatusCode != HttpStatusCode.OK).Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_same_recovery_code_submitted_concurrently_succeeds_once()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => VerifyAsync(Client(), token, recovery: a.RecoveryCodes[3])));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, string.Join(" | ", results.Select(r => (int)r.StatusCode)));
        (await RecoveryCountsAsync(a.UserId)).Unconsumed.Should().Be(9);
    }

    // ------------------------------------------------------------------ brute force

    [Fact]
    public async Task MFA_failures_are_counted_and_the_password_step_does_not_reset_them()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);
        for (var i = 0; i < 2; i++) await DetailsAsync(await VerifyAsync(Client(), token, Wrong), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        (await StateAsync(a.UserId)).Failed.Should().Be(2);

        // Password succeeds again (that is how an attacker with the password gets a fresh challenge): the count stays.
        var second = await ChallengeAsync(a.Email);
        (await StateAsync(a.UserId)).Failed.Should().Be(2, "password success must not reset the MFA failure count");

        await (await VerifyAsync(Client(), second, await FreshCodeAsync(a))).DataAsync(200);
        (await StateAsync(a.UserId)).Failed.Should().Be(0, "only a completed MFA step resets it");
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_for_sixty_minutes_even_against_the_right_code()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);
        for (var i = 0; i < 5; i++) await DetailsAsync(await VerifyAsync(Client(), token, Wrong), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");

        var state = await StateAsync(a.UserId);
        state.LockedUntil.Should().NotBeNull();
        state.LockedUntil!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(59)).And.BeBefore(DateTime.UtcNow.AddMinutes(61));

        await DetailsAsync(await VerifyAsync(Client(), token, await FreshCodeAsync(a)), HttpStatusCode.Locked, "ACCOUNT_LOCKED");
        await DetailsAsync(await PasswordLoginAsync(Client(), a.Email), HttpStatusCode.Locked, "ACCOUNT_LOCKED");
        (await AuditAsync(a.UserId)).Select(r => r.Action).Should().Contain(AuditActions.AccountLocked);
    }

    [Fact]
    public async Task Parallel_wrong_guesses_cannot_exceed_the_lockout_threshold()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => VerifyAsync(Client(), token, Wrong)));

        results.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(5, "exactly the threshold may be evaluated: " + string.Join(",", results.Select(r => (int)r.StatusCode)));
        results.Count(r => r.StatusCode == HttpStatusCode.Locked).Should().Be(15);
    }

    [Fact]
    public async Task Rate_limiting_stays_active_on_the_MFA_endpoints()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:AuthPerMinute", "3"));
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            statuses.Add((await client.PostAsJsonAsync($"{Mfa}/verify", new { challengeToken = "x", totpCode = Wrong })).StatusCode);

        statuses.Take(3).Should().OnlyContain(s => s == HttpStatusCode.Unauthorized);
        statuses.Skip(3).Should().Contain(HttpStatusCode.TooManyRequests);
    }

    // ------------------------------------------------------------------ isolation between accounts

    [Fact]
    public async Task Another_admins_code_or_recovery_code_never_answers_my_challenge()
    {
        var a = await EnrollAsync();
        var b = await EnrollAsync();

        // A's challenge + B's current TOTP, and B's challenge + A's recovery code.
        await DetailsAsync(await VerifyAsync(Client(), await ChallengeAsync(a.Email), await FreshCodeAsync(b)), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        await DetailsAsync(await VerifyAsync(Client(), await ChallengeAsync(b.Email), recovery: a.RecoveryCodes[0]), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");

        (await RecoveryCountsAsync(a.UserId)).Unconsumed.Should().Be(10, "A's codes were not touched");
        // B's own code still works: the failed attempt did not burn B's time step.
        await (await VerifyAsync(Client(), await ChallengeAsync(b.Email), await FreshCodeAsync(b))).DataAsync(200);
    }

    [Fact]
    public async Task Tokens_are_bound_to_their_purpose_and_their_user()
    {
        var a = await EnrollAsync();
        var b = await EnrollAsync();
        var protector = factory.Services.GetRequiredService<IMfaProtector>();
        var challengeA = await ChallengeAsync(a.Email);

        // A challenge token is not an enrolment token, and an enrolment token is not a challenge token.
        await DetailsAsync(await Client().PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = challengeA }), HttpStatusCode.Unauthorized, "INVALID_MFA_TOKEN");
        var enrolToken = protector.ProtectToken(MfaTokenPurpose.EnrollmentStart, new MfaTokenPayload(a.UserId, null), DateTime.UtcNow.AddMinutes(5));
        await DetailsAsync(await VerifyAsync(Client(), enrolToken, Wrong), HttpStatusCode.Unauthorized, "INVALID_MFA_TOKEN");

        // A's enrolment cannot be completed as B: the token carries A's identity, B's data stays untouched.
        var (email, userId) = await NewAdminAsync();
        var step = await DetailsAsync(await PasswordLoginAsync(Client(), email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");
        var setup = await (await Client().PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = step.GetProperty("enrollmentToken").GetString() })).DataAsync(200);
        var asB = b.Client; // signed in as B, but the endpoint ignores bearer identity
        var done = await asB.PostAsJsonAsync($"{Mfa}/enroll/complete",
            new { pendingToken = setup.GetProperty("pendingToken").GetString(), code = TestApi.CurrentCode(Totp.FromBase32(setup.GetProperty("secret").GetString()!)!) });
        (await done.DataAsync(200)).GetProperty("session").GetProperty("user").GetProperty("id").GetGuid().Should().Be(userId, "the session is the enrolling account's, never B's");
        (await RecoveryCountsAsync(b.UserId)).Should().Be((10, 10));
        (await FactorCountAsync(b.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Expired_tampered_forged_and_foreign_tokens_are_rejected_and_are_not_access_tokens()
    {
        var a = await EnrollAsync();
        var protector = factory.Services.GetRequiredService<IMfaProtector>();
        var valid = await ChallengeAsync(a.Email);

        var expired = protector.ProtectToken(MfaTokenPurpose.Challenge, new MfaTokenPayload(a.UserId, null), DateTime.UtcNow.AddSeconds(-5));
        var tampered = valid[..^3] + (valid[^3] == 'A' ? "BBB" : "AAA");
        var foreign = new DataProtectionProviderFor().Protect(a.UserId);
        var noSuchUser = protector.ProtectToken(MfaTokenPurpose.Challenge, new MfaTokenPayload(Guid.NewGuid(), null), DateTime.UtcNow.AddMinutes(5));

        foreach (var bad in new[] { expired, tampered, foreign, noSuchUser, "garbage", Convert.ToBase64String(new byte[64]) })
            await DetailsAsync(await VerifyAsync(Client(), bad, await FreshCodeAsync(a)), HttpStatusCode.Unauthorized, "INVALID_MFA_TOKEN");

        // A token is never accepted as a JWT access token.
        var asBearer = factory.WithToken(valid);
        await ShouldBeEndedAsync(await asBearer.GetAsync("/api/v1/platform/tenants"));
        await ShouldBeEndedAsync(await asBearer.GetAsync("/api/v1/auth/me"));
    }

    /// <summary>A token made by a different key ring (another Data Protection provider), shaped like ours.</summary>
    private sealed class DataProtectionProviderFor
    {
        public string Protect(Guid userId) => new ClinicSaaS.Api.Infrastructure.DataProtectionMfaProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider())
            .ProtectToken(MfaTokenPurpose.Challenge, new MfaTokenPayload(userId, null), DateTime.UtcNow.AddMinutes(5));
    }

    // ------------------------------------------------------------------ sessions

    [Fact]
    public async Task A_platform_session_without_a_factor_is_rejected_and_a_refresh_token_cannot_bypass_MFA()
    {
        var a = await EnrollAsync();
        (await a.Client.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The factor disappears behind the application's back (legacy session, deleted row, unexpected data).
        await factory.WithDbAsync(null, async db => await db.UserTotpFactors.Where(f => f.UserId == a.UserId).ExecuteDeleteAsync());

        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/platform/tenants"));
        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/auth/me"));
        var refresh = await Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = a.Session.GetProperty("refreshToken").GetString() });
        await DetailsAsync(refresh, HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
        await factory.WithDbAsync(null, async db =>
            (await db.RefreshTokens.AsNoTracking().Where(t => t.UserId == a.UserId && t.RevokedReason == "mfa_required").AnyAsync()).Should().BeTrue());

        // And signing in again is an enrolment, not a session.
        await DetailsAsync(await PasswordLoginAsync(Client(), a.Email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");
    }

    [Fact]
    public async Task Refresh_keeps_working_for_an_MFA_derived_session()
    {
        var a = await EnrollAsync();
        var refreshed = await Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = a.Session.GetProperty("refreshToken").GetString() });
        var data = await refreshed.DataAsync(200);
        (await factory.WithToken(data.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_still_ends_an_MFA_session_at_once()
    {
        var a = await EnrollAsync();
        (await a.Client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = a.Session.GetProperty("refreshToken").GetString() })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/platform/tenants"));
    }

    [Fact]
    public async Task Changing_the_password_ends_sessions_and_signing_in_still_needs_MFA()
    {
        var a = await EnrollAsync();
        var changed = await a.Client.PostAsJsonAsync("/api/v1/auth/change-password", new { currentPassword = TestApi.Password, newPassword = NewPassword });
        var fresh = await changed.DataAsync(200);

        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/platform/tenants"));
        (await factory.WithToken(fresh.GetProperty("accessToken").GetString()!).GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK);
        await DetailsAsync(await PasswordLoginAsync(Client(), a.Email, NewPassword), HttpStatusCode.Conflict, "MFA_REQUIRED");
    }

    [Fact]
    public async Task A_password_reset_by_email_ends_sessions_but_does_not_replace_the_second_factor()
    {
        var a = await EnrollAsync();
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = a.Email })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var token = factory.Emails.TokenFor(a.Email, "/reset-password");
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/platform/tenants"));
        // Someone who controls the mailbox now knows the password — and still gets only a challenge.
        var res = await PasswordLoginAsync(Client(), a.Email, NewPassword);
        await DetailsAsync(res, HttpStatusCode.Conflict, "MFA_REQUIRED");
        (await res.Content.ReadAsStringAsync()).Should().NotContain("accessToken");
    }

    [Fact]
    public async Task A_disabled_admin_can_neither_finish_a_challenge_nor_use_a_session()
    {
        var a = await EnrollAsync();
        var token = await ChallengeAsync(a.Email);
        await factory.WithDbAsync(null, async db => await db.Users.Where(u => u.Id == a.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));

        await DetailsAsync(await VerifyAsync(Client(), token, await FreshCodeAsync(a)), HttpStatusCode.Forbidden, "ACCOUNT_DISABLED");
        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/platform/tenants"));
    }

    // ------------------------------------------------------------------ recovery codes and removal

    [Fact]
    public async Task Regenerating_recovery_codes_needs_a_current_TOTP_replaces_all_ten_and_keeps_MFA_on()
    {
        var a = await EnrollAsync();
        var url = $"{Mfa}/recovery-codes/regenerate";

        (await factory.CreateClient().PostAsJsonAsync(url, new { totpCode = Wrong })).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "needs a session");
        (await a.Client.PostAsJsonAsync(url, new { recoveryCode = a.RecoveryCodes[0] })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a recovery code cannot authorise this");
        await DetailsAsync(await a.Client.PostAsJsonAsync(url, new { totpCode = Wrong }), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        (await RecoveryCountsAsync(a.UserId)).Unconsumed.Should().Be(10);

        var res = await a.Client.PostAsJsonAsync(url, new { totpCode = await FreshCodeAsync(a) });
        res.Headers.CacheControl!.NoStore.Should().BeTrue();
        var fresh = (await res.DataAsync(200)).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        fresh.Should().HaveCount(10).And.OnlyHaveUniqueItems().And.NotIntersectWith(a.RecoveryCodes);
        (await RecoveryCountsAsync(a.UserId)).Should().Be((10, 10), "exactly ten, the old ones are gone");
        (await FactorCountAsync(a.UserId)).Should().Be(1, "regenerating does not disable MFA");

        await DetailsAsync(await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: a.RecoveryCodes[0]), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        await (await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: fresh[0])).DataAsync(200);
        (await a.Client.GetAsync("/api/v1/platform/tenants")).StatusCode.Should().Be(HttpStatusCode.OK, "the regenerating session itself stays valid");
        (await AuditAsync(a.UserId)).Select(r => r.Action).Should().Contain(AuditActions.MfaRecoveryCodesRegenerated);
    }

    [Fact]
    public async Task Removing_MFA_needs_the_password_and_a_code_deletes_everything_and_ends_every_session_without_a_new_one()
    {
        var a = await EnrollAsync();
        var other = await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: a.RecoveryCodes[1].Replace("-", ""));
        var otherToken = (await other.DataAsync(200)).GetProperty("accessToken").GetString()!;
        var url = $"{Mfa}/remove";

        (await factory.CreateClient().PostAsJsonAsync(url, new { currentPassword = TestApi.Password, totpCode = Wrong })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await DetailsAsync(await a.Client.PostAsJsonAsync(url, new { currentPassword = "Not-The-Passw0rd!", totpCode = await FreshCodeAsync(a) }), HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        await DetailsAsync(await a.Client.PostAsJsonAsync(url, new { currentPassword = TestApi.Password, totpCode = Wrong }), HttpStatusCode.Unauthorized, "INVALID_MFA_CODE");
        (await FactorCountAsync(a.UserId)).Should().Be(1, "nothing was removed by the failed attempts");

        var res = await a.Client.PostAsJsonAsync(url, new { currentPassword = TestApi.Password, recoveryCode = a.RecoveryCodes[0] });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await res.Content.ReadAsStringAsync()).Should().BeEmpty("no new session is created");

        (await FactorCountAsync(a.UserId)).Should().Be(0);
        (await RecoveryCountsAsync(a.UserId)).Total.Should().Be(0);
        await ShouldBeEndedAsync(await a.Client.GetAsync("/api/v1/platform/tenants"));
        await ShouldBeEndedAsync(await factory.WithToken(otherToken).GetAsync("/api/v1/platform/tenants"));
        await DetailsAsync(await Client().PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = a.Session.GetProperty("refreshToken").GetString() }), HttpStatusCode.Unauthorized, "INVALID_REFRESH_TOKEN");
        await DetailsAsync(await PasswordLoginAsync(Client(), a.Email), HttpStatusCode.Conflict, "MFA_ENROLLMENT_REQUIRED");
        (await AuditAsync(a.UserId)).Select(r => r.Action).Should().Contain(AuditActions.MfaDisabled);
    }

    [Fact]
    public async Task A_clinic_user_cannot_use_the_signed_in_MFA_operations()
    {
        var clinic = await factory.RegisterClinicAsync();
        (await clinic.Client.PostAsJsonAsync($"{Mfa}/recovery-codes/regenerate", new { totpCode = Wrong })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await clinic.Client.PostAsJsonAsync($"{Mfa}/remove", new { currentPassword = TestApi.Password, totpCode = Wrong })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ------------------------------------------------------------------ secret protection

    [Fact]
    public async Task The_database_never_holds_the_secret_in_plaintext_and_it_is_bound_to_its_owner()
    {
        var a = await EnrollAsync();
        var other = await EnrollAsync();
        var protector = factory.Services.GetRequiredService<IMfaProtector>();

        await factory.WithDbAsync(null, async db =>
        {
            var row = await db.UserTotpFactors.AsNoTracking().SingleAsync(f => f.UserId == a.UserId);
            row.SecretProtected.Length.Should().BeLessThanOrEqualTo(256).And.BeGreaterThan(a.Secret.Length);
            IndexOf(row.SecretProtected, a.Secret).Should().Be(-1, "raw secret bytes");
            IndexOf(row.SecretProtected, System.Text.Encoding.ASCII.GetBytes(a.SetupSecret)).Should().Be(-1, "base32 secret text");
            protector.TryUnprotectSecret(a.UserId, row.SecretProtected).Should().Equal(a.Secret);
            protector.TryUnprotectSecret(other.UserId, row.SecretProtected).Should().BeNull("a ciphertext copied to another user's row does not decrypt");
            protector.TryUnprotectSecret(a.UserId, [.. row.SecretProtected.Take(row.SecretProtected.Length - 1), (byte)(row.SecretProtected[^1] ^ 1)]).Should().BeNull("tampered");
        });
    }

    [Fact]
    public void A_different_key_ring_cannot_read_the_secret_or_the_tokens()
    {
        var userId = Guid.NewGuid();
        var ours = new ClinicSaaS.Api.Infrastructure.DataProtectionMfaProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var theirs = new ClinicSaaS.Api.Infrastructure.DataProtectionMfaProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        var secret = Totp.GenerateSecret();

        ours.TryUnprotectSecret(userId, ours.ProtectSecret(userId, secret)).Should().Equal(secret);
        theirs.TryUnprotectSecret(userId, ours.ProtectSecret(userId, secret)).Should().BeNull();

        var token = ours.ProtectToken(MfaTokenPurpose.Challenge, new MfaTokenPayload(userId, null), DateTime.UtcNow.AddMinutes(1));
        ours.TryUnprotectToken(MfaTokenPurpose.Challenge, token, DateTime.UtcNow)!.UserId.Should().Be(userId);
        ours.TryUnprotectToken(MfaTokenPurpose.EnrollmentStart, token, DateTime.UtcNow).Should().BeNull("wrong purpose");
        ours.TryUnprotectToken(MfaTokenPurpose.Challenge, token, DateTime.UtcNow.AddMinutes(2)).Should().BeNull("expired");
        theirs.TryUnprotectToken(MfaTokenPurpose.Challenge, token, DateTime.UtcNow).Should().BeNull("forged / foreign key ring");
    }

    [Fact]
    public async Task A_secret_that_cannot_be_decrypted_fails_closed_and_recovery_codes_still_work()
    {
        var a = await EnrollAsync();
        await factory.WithDbAsync(null, async db =>
            await db.UserTotpFactors.Where(f => f.UserId == a.UserId).ExecuteUpdateAsync(s => s.SetProperty(f => f.SecretProtected, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));

        var res = await VerifyAsync(Client(), await ChallengeAsync(a.Email), await FreshCodeAsync(a));
        await DetailsAsync(res, HttpStatusCode.Unauthorized, "INVALID_MFA_CODE"); // not a 500, not a session
        await (await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: a.RecoveryCodes[2])).DataAsync(200);
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = [];
        public void Emit(LogEvent logEvent) { lock (_events) _events.Add(logEvent); }
        public string[] AllText() { lock (_events) return _events.Select(e => e.RenderMessage() + " | " + e.Exception + " | " + string.Join(",", e.Properties.Select(p => p.Key + "=" + p.Value))).ToArray(); }
    }

    [Fact]
    public async Task Secrets_codes_and_tokens_never_reach_the_logs_the_audit_trail_or_ordinary_responses()
    {
        var sink = new CapturingSink();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            b.ConfigureTestServices(s => s.AddSingleton<ILogEventSink>(sink));
        });
        HttpClient New() { var c = host.CreateClient(); c.DefaultRequestHeaders.Add("X-Client-Type", "mobile"); return c; }

        var (email, userId) = await NewAdminAsync();
        var secrets = new List<string> { TestApi.Password, NewPassword, Wrong };
        var otherBodies = new List<string>(); // every response except the one that is meant to show the setup secret

        // enrol
        var client = New();
        var step = await PasswordLoginAsync(client, email);
        otherBodies.Add(await step.Content.ReadAsStringAsync());
        var enrolToken = (await step.ReadJsonAsync()).GetProperty("details").GetProperty("enrollmentToken").GetString()!;
        var startRes = await client.PostAsJsonAsync($"{Mfa}/enroll/start", new { enrollmentToken = enrolToken });
        var setup = await startRes.DataAsync(200);
        var secret = setup.GetProperty("secret").GetString()!;
        var pending = setup.GetProperty("pendingToken").GetString()!;
        secrets.AddRange([secret, setup.GetProperty("setupUri").GetString()!, pending, enrolToken]);
        var rawSecret = Totp.FromBase32(secret)!;
        var code = TestApi.CurrentCode(rawSecret);
        secrets.Add(code);
        var done = await client.PostAsJsonAsync($"{Mfa}/enroll/complete", new { pendingToken = pending, code });
        var doneData = await done.DataAsync(200);
        var recovery = doneData.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        secrets.AddRange(recovery);
        secrets.AddRange(recovery.Select(RecoveryCodes.Normalize));
        var session = doneData.GetProperty("session");
        var access = session.GetProperty("accessToken").GetString()!;
        secrets.AddRange([access, session.GetProperty("refreshToken").GetString()!]);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access);

        // sign in again: challenge, a wrong code, a recovery code; then regenerate and remove
        var c2 = New();
        var challengeRes = await PasswordLoginAsync(c2, email);
        otherBodies.Add(await challengeRes.Content.ReadAsStringAsync());
        var challenge = (await challengeRes.ReadJsonAsync()).GetProperty("details").GetProperty("challengeToken").GetString()!;
        secrets.Add(challenge);
        otherBodies.Add(await (await VerifyAsync(c2, challenge, Wrong)).Content.ReadAsStringAsync());
        var viaRecovery = await VerifyAsync(c2, challenge, recovery: recovery[0]);
        otherBodies.Add(await viaRecovery.Content.ReadAsStringAsync());
        var sessionData = (await viaRecovery.ReadJsonAsync()).GetProperty("data");
        secrets.AddRange([sessionData.GetProperty("accessToken").GetString()!, sessionData.GetProperty("refreshToken").GetString()!]);
        c2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionData.GetProperty("accessToken").GetString());

        factory.MfaSecrets[email] = rawSecret;
        await factory.ForgetLastTimeStepAsync(email);
        var regen = await c2.PostAsJsonAsync($"{Mfa}/recovery-codes/regenerate", new { totpCode = TestApi.CurrentCode(rawSecret) });
        var regenBody = await regen.Content.ReadAsStringAsync();
        regen.StatusCode.Should().Be(HttpStatusCode.OK);
        var regenerated = (await regen.ReadJsonAsync()).GetProperty("data").GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        secrets.AddRange(regenerated);
        otherBodies.Add(await (await c2.GetAsync("/api/v1/auth/me")).Content.ReadAsStringAsync());
        otherBodies.Add(await (await c2.GetAsync("/api/v1/auth/sessions")).Content.ReadAsStringAsync());
        var removed = await c2.PostAsJsonAsync($"{Mfa}/remove", new { currentPassword = TestApi.Password, recoveryCode = regenerated[1] });
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // 1. logs
        var logs = string.Join("\n", sink.AllText());
        foreach (var s in secrets) logs.Should().NotContain(s, "a secret/code/token must never be logged");
        // 2. audit trail (every column of every row of this user)
        var audit = string.Join("\n", (await AuditAsync(userId)).Select(r => $"{r.Action}|{r.EntityType}|{r.EntityId}|{r.Details}|{r.UserAgent}|{r.IpAddress}"));
        foreach (var s in secrets) audit.Should().NotContain(s, "a secret/code/token must never be audited");
        // 3. ordinary responses never carry the setup secret or the setup URI
        foreach (var body in otherBodies.Append(regenBody))
        {
            body.Should().NotContain(secret).And.NotContain("otpauth://");
        }
        (await done.Content.ReadAsStringAsync()).Should().NotContain(secret, "the enrolment completion does not repeat the secret");
        (await startRes.Content.ReadAsStringAsync()).Should().Contain(secret, "the setup response is the one place it is shown");
    }

    // ------------------------------------------------------------------ audit

    [Fact]
    public async Task Every_required_MFA_event_is_audited_and_the_existing_ones_keep_working()
    {
        var a = await EnrollAsync();                                                       // challenge_issued, enrollment_started, enabled, login.succeeded
        var (_, otherId) = await NewAdminAsync();
        // an enrolment failure for this account is covered in the enrolment test; here: a failed challenge, a TOTP success, a recovery use, a regeneration, a removal
        var token = await ChallengeAsync(a.Email);
        await VerifyAsync(Client(), token, Wrong);
        await (await VerifyAsync(Client(), token, await FreshCodeAsync(a))).DataAsync(200);
        await (await VerifyAsync(Client(), await ChallengeAsync(a.Email), recovery: a.RecoveryCodes[0])).DataAsync(200);
        (await a.Client.PostAsJsonAsync($"{Mfa}/recovery-codes/regenerate", new { totpCode = await FreshCodeAsync(a) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await a.Client.PostAsJsonAsync($"{Mfa}/remove", new { currentPassword = TestApi.Password, totpCode = await FreshCodeAsync(a) })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var actions = (await AuditAsync(a.UserId)).Select(r => r.Action).Distinct().ToList();
        actions.Should().Contain([
            AuditActions.MfaChallengeIssued, AuditActions.MfaEnrollmentStarted, AuditActions.MfaEnabled, AuditActions.MfaChallengeFailed,
            AuditActions.MfaChallengeSucceeded, AuditActions.MfaRecoveryCodeUsed, AuditActions.MfaRecoveryCodesRegenerated,
            AuditActions.MfaDisabled, AuditActions.LoginSucceeded]);
        otherId.Should().NotBeEmpty();
        (await AuditAsync(a.UserId)).Where(r => r.Action == AuditActions.MfaChallengeFailed).Should().OnlyContain(r => r.Result == AuditResult.Failure);
    }

    [Fact]
    public void The_event_names_are_the_documented_ones()
    {
        new[]
        {
            AuditActions.MfaChallengeIssued, AuditActions.MfaEnrollmentStarted, AuditActions.MfaEnrollmentFailed, AuditActions.MfaEnabled,
            AuditActions.MfaChallengeFailed, AuditActions.MfaChallengeSucceeded, AuditActions.MfaRecoveryCodeUsed,
            AuditActions.MfaRecoveryCodesRegenerated, AuditActions.MfaDisabled, AuditActions.MfaReset,
        }.Should().Equal(
            "auth.mfa.challenge_issued", "auth.mfa.enrollment_started", "auth.mfa.enrollment_failed", "auth.mfa.enabled",
            "auth.mfa.challenge_failed", "auth.mfa.challenge_succeeded", "auth.mfa.recovery_code_used",
            "auth.mfa.recovery_codes_regenerated", "auth.mfa.disabled", "auth.mfa.reset");
    }

    [Fact]
    public void The_break_glass_runbook_writes_the_mfa_reset_audit_event()
    {
        // The application has no operator-reset endpoint by design; the runbook is the only producer of auth.mfa.reset.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "operations", "platform-admin-mfa.md"))) dir = dir.Parent;
        dir.Should().NotBeNull("docs/operations/platform-admin-mfa.md exists");
        var runbook = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "operations", "platform-admin-mfa.md"));
        runbook.Should().Contain(AuditActions.MfaReset);
    }

    // ------------------------------------------------------------------ helpers (bytes)

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
