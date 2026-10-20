using System.Security.Cryptography;
using System.Text;

namespace ClinicSaaS.Application.Auth;

/// <summary>
/// RFC 6238 time-based one-time passwords (RFC 4226 HOTP over HMAC-SHA1, 6 digits, 30-second steps) — the profile
/// every authenticator app supports. Standard algorithm on the platform's HMAC primitive; nothing custom.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int SecretBytes = 20; // 160 bits, as RFC 4226 recommends

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>The 30-second time step a UTC instant falls in.</summary>
    public static long StepOf(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / StepSeconds;

    /// <summary>The 6-digit code for <paramref name="step"/> (RFC 4226 dynamic truncation).</summary>
    public static string CodeFor(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Finds the time step <paramref name="code"/> belongs to: the previous, current or next step (clock drift), and
    /// strictly newer than <paramref name="lastUsedStep"/> (a step is never accepted twice). The comparison does not
    /// short-circuit. The caller must still record the step atomically — this only decides which step matched.
    /// </summary>
    public static bool TryMatch(byte[] secret, string code, DateTime nowUtc, long? lastUsedStep, out long matchedStep)
    {
        matchedStep = 0;
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return false;

        var current = StepOf(nowUtc);
        var found = false;
        for (var step = current - 1; step <= current + 1; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeFor(secret, step)), Encoding.ASCII.GetBytes(code));
            if (equal && !found && (lastUsedStep is null || step > lastUsedStep))
            {
                found = true;
                matchedStep = step;
            }
        }
        return found;
    }

    /// <summary>Strips spaces / dashes the user may type around a code.</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[]? FromBase32(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Base32Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0) return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    /// <summary>The <c>otpauth://</c> URI authenticator apps read from a QR code (also usable as a setup link).</summary>
    public static string SetupUri(string issuer, string account, string base32Secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={base32Secret}"
        + $"&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>
/// One-time recovery codes: 80 bits of randomness each (16 base32 characters, shown as four groups of four), stored
/// only as a SHA-256 hash. An 80-bit random value cannot be brute-forced from its hash, which is why a plain hash is
/// enough here (unlike a human-chosen password).
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const int RandomBytes = 10; // 80 bits → exactly 16 base32 characters

    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        for (var i = 0; i < Count; i++)
        {
            var raw = Totp.ToBase32(RandomNumberGenerator.GetBytes(RandomBytes));
            codes.Add($"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..16]}");
        }
        return codes;
    }

    /// <summary>Canonical form that is hashed: upper-case, without dashes or spaces.</summary>
    public static string Normalize(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>True for something that could be a recovery code (so junk is rejected before it is hashed).</summary>
    public static bool LooksValid(string normalized) =>
        normalized.Length == 16 && normalized.All(c => char.IsAsciiLetterUpper(c) || (c >= '2' && c <= '7'));
}
