using System.Text;
using ClinicSaaS.Application.Auth;
using FluentAssertions;

namespace ClinicSaaS.UnitTests;

/// <summary>RFC 6238 TOTP and the one-time recovery codes (platform-admin MFA).</summary>
public class MfaPrimitivesTests
{
    // RFC 6238 Appendix B, SHA-1 secret "12345678901234567890". The RFC lists 8-digit codes; ours are the last 6 digits
    // of the same value (RFC 4226 truncation, then mod 10^6).
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]            // 94287082
    [InlineData(1111111109L, "081804")]    // 07081804
    [InlineData(1111111111L, "050471")]    // 14050471
    [InlineData(1234567890L, "005924")]    // 89005924
    [InlineData(2000000000L, "279037")]    // 69279037
    [InlineData(20000000000L, "353130")]   // 65353130
    public void Matches_the_RFC_6238_test_vectors(long unixSeconds, string expected)
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        Totp.CodeFor(RfcSecret, Totp.StepOf(at)).Should().Be(expected);
    }

    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 10, DateTimeKind.Utc);

    [Fact]
    public void The_current_previous_and_next_time_steps_are_accepted()
    {
        var step = Totp.StepOf(Now);
        foreach (var offset in new[] { -1, 0, 1 })
        {
            Totp.TryMatch(RfcSecret, Totp.CodeFor(RfcSecret, step + offset), Now, null, out var matched).Should().BeTrue($"step {offset:+0;-0;0} is within the drift window");
            matched.Should().Be(step + offset);
        }
    }

    [Fact]
    public void Two_steps_away_is_rejected_in_both_directions()
    {
        var step = Totp.StepOf(Now);
        Totp.TryMatch(RfcSecret, Totp.CodeFor(RfcSecret, step - 2), Now, null, out _).Should().BeFalse();
        Totp.TryMatch(RfcSecret, Totp.CodeFor(RfcSecret, step + 2), Now, null, out _).Should().BeFalse();
    }

    [Fact]
    public void A_step_is_never_accepted_twice_nor_an_older_one()
    {
        var step = Totp.StepOf(Now);
        Totp.TryMatch(RfcSecret, Totp.CodeFor(RfcSecret, step), Now, step, out _).Should().BeFalse("the same step was already used");
        Totp.TryMatch(RfcSecret, Totp.CodeFor(RfcSecret, step - 1), Now, step, out _).Should().BeFalse("an older step is replay too");
        Totp.TryMatch(RfcSecret, Totp.CodeFor(RfcSecret, step + 1), Now, step, out var matched).Should().BeTrue();
        matched.Should().Be(step + 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 345")]
    [InlineData("abcdef")]
    [InlineData("١٢٣٤٥٦")] // Arabic-Indic digits are not accepted
    public void Malformed_codes_are_rejected(string code)
    {
        Totp.TryMatch(RfcSecret, code, Now, null, out _).Should().BeFalse();
    }

    [Fact]
    public void A_wrong_secret_never_matches()
    {
        var other = Totp.GenerateSecret();
        Totp.TryMatch(other, Totp.CodeFor(RfcSecret, Totp.StepOf(Now)), Now, null, out _).Should().BeFalse();
    }

    [Fact]
    public void Secrets_are_160_bits_and_round_trip_through_base32()
    {
        var secret = Totp.GenerateSecret();
        secret.Should().HaveCount(20);
        Totp.GenerateSecret().Should().NotEqual(secret);
        var text = Totp.ToBase32(secret);
        text.Should().MatchRegex("^[A-Z2-7]{32}$");
        Totp.FromBase32(text).Should().Equal(secret);
        Totp.FromBase32("not base32!").Should().BeNull();
    }

    [Fact]
    public void The_setup_uri_names_the_issuer_account_and_parameters()
    {
        var uri = Totp.SetupUri("ClinicSaaS", "root@platform.test", "JBSWY3DPEHPK3PXP");
        uri.Should().StartWith("otpauth://totp/ClinicSaaS:root%40platform.test?secret=JBSWY3DPEHPK3PXP")
            .And.Contain("issuer=ClinicSaaS").And.Contain("digits=6").And.Contain("period=30").And.Contain("algorithm=SHA1");
    }

    // ------------------------------------------------------------------ recovery codes

    [Fact]
    public void Exactly_ten_distinct_codes_of_at_least_80_bits_are_generated()
    {
        var codes = RecoveryCodes.Generate();
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        foreach (var code in codes)
        {
            code.Should().MatchRegex("^[A-Z2-7]{4}(-[A-Z2-7]{4}){3}$");
            var normalized = RecoveryCodes.Normalize(code);
            normalized.Should().HaveLength(16); // 16 base32 characters × 5 bits = 80 bits
            RecoveryCodes.LooksValid(normalized).Should().BeTrue();
        }
        RecoveryCodes.Generate().Should().NotIntersectWith(codes);
    }

    [Theory]
    [InlineData("abcd-efgh-2345-6723", "ABCDEFGH23456723")]
    [InlineData("  ABCD EFGH 2345 6723 ", "ABCDEFGH23456723")]
    public void Codes_are_normalised_before_they_are_hashed(string typed, string expected)
    {
        RecoveryCodes.Normalize(typed).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEFGH2345672")]      // 15
    [InlineData("ABCDEFGH234567234")]    // 17
    [InlineData("ABCDEFGH23456781")]     // 0/1/8/9 are not base32
    [InlineData("ABCDEFGH2345672!")]
    public void Things_that_cannot_be_recovery_codes_are_recognised_before_any_lookup(string normalized)
    {
        RecoveryCodes.LooksValid(normalized).Should().BeFalse();
    }
}
