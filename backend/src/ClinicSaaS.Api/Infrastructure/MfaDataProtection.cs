using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ClinicSaaS.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>
/// <see cref="IMfaProtector"/> on ASP.NET Core Data Protection: authenticated encryption, explicit purposes, key ring
/// managed (and rotated) by the framework. No algorithm, key derivation or key storage of our own.
///
/// - TOTP secret: purpose <c>ClinicSaaS.Mfa.TotpSecret.v1</c> with the user id as a sub-purpose, so a ciphertext copied
///   to another user's row does not decrypt.
/// - MFA tokens: purpose <c>ClinicSaaS.Mfa.Token.v1</c> with the token kind as a sub-purpose, so a challenge token is
///   not an enrolment token. Expiry is part of the encrypted payload and checked against the project clock.
///   A token is not a JWT: the bearer handler rejects it, so it can never authenticate a request.
/// </summary>
public sealed class DataProtectionMfaProtector(IDataProtectionProvider provider) : IMfaProtector
{
    public const string SecretPurpose = "ClinicSaaS.Mfa.TotpSecret.v1"; // gitleaks:allow — a public Data Protection purpose label, not a secret
    public const string TokenPurpose = "ClinicSaaS.Mfa.Token.v1";

    private sealed record TokenBody(Guid U, Guid? T, string? D, long E);

    public byte[] ProtectSecret(Guid userId, byte[] secret) =>
        provider.CreateProtector(SecretPurpose, userId.ToString("N")).Protect(secret);

    public byte[]? TryUnprotectSecret(Guid userId, byte[] protectedSecret)
    {
        try { return provider.CreateProtector(SecretPurpose, userId.ToString("N")).Unprotect(protectedSecret); }
        catch (CryptographicException) { return null; }
    }

    public string ProtectToken(MfaTokenPurpose purpose, MfaTokenPayload payload, DateTime expiresAtUtc)
    {
        var body = new TokenBody(payload.UserId, payload.TenantId, payload.Data,
            new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds());
        var bytes = provider.CreateProtector(TokenPurpose, purpose.ToString()).Protect(JsonSerializer.SerializeToUtf8Bytes(body));
        return Base64Url.EncodeToString(bytes);
    }

    public MfaTokenPayload? TryUnprotectToken(MfaTokenPurpose purpose, string token, DateTime nowUtc)
    {
        try
        {
            var plain = provider.CreateProtector(TokenPurpose, purpose.ToString()).Unprotect(Base64Url.DecodeFromChars(token));
            var body = JsonSerializer.Deserialize<TokenBody>(plain);
            if (body is null || body.U == Guid.Empty) return null;
            if (body.E <= new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()) return null;
            return new MfaTokenPayload(body.U, body.T, body.D);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Where the Data Protection key ring lives. The ring encrypts the TOTP secrets, so it is a production prerequisite:
/// persisted OUTSIDE the SQL database and protected by a key that is not in the database either. Hosting is not chosen
/// yet, so the only mechanism built is a persisted directory plus an X.509 certificate (from a file or a certificate
/// store); a cloud KMS can replace it once a provider is approved. Nothing falls back to an insecure default outside
/// Development: a missing or unloadable setting stops the API from starting.
/// </summary>
public static class MfaDataProtection
{
    public const string Section = "DataProtection";
    public const string ApplicationName = "ClinicSaaS";

    public static IServiceCollection AddMfaDataProtection(this IServiceCollection services, IConfiguration config, RuntimeProfile profile)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        var keyRingPath = config[$"{Section}:KeyRingPath"];

        switch (profile)
        {
            case RuntimeProfile.AutomatedTest:
                builder.UseEphemeralDataProtectionProvider();
                break;

            case RuntimeProfile.Development:
                // The framework default (the developer's profile folder) unless a path is given.
                if (!string.IsNullOrWhiteSpace(keyRingPath)) builder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
                break;

            default:
                if (string.IsNullOrWhiteSpace(keyRingPath) || !Path.IsPathRooted(keyRingPath))
                    throw new InvalidOperationException($"{Section}:KeyRingPath must be an absolute path outside the database (persisted key ring).");
                builder.PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
                builder.ProtectKeysWithCertificate(LoadCertificate(config));
                break;
        }

        services.AddSingleton<IMfaProtector, DataProtectionMfaProtector>();
        return services;
    }

    private static X509Certificate2 LoadCertificate(IConfiguration config)
    {
        var path = config[$"{Section}:CertificatePath"];
        var thumbprint = config[$"{Section}:CertificateThumbprint"];
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                return X509CertificateLoader.LoadPkcs12FromFile(path, config[$"{Section}:CertificatePassword"]);

            if (!string.IsNullOrWhiteSpace(thumbprint))
            {
                foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
                {
                    using var store = new X509Store(StoreName.My, location);
                    store.Open(OpenFlags.ReadOnly);
                    var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                    if (found.Count > 0 && found[0].HasPrivateKey) return found[0];
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // The exception text can name a path; the setting names are enough to act on.
        }
        throw new InvalidOperationException(
            $"{Section}:CertificatePath (with {Section}:CertificatePassword) or {Section}:CertificateThumbprint must name a loadable certificate with a private key; it protects the key ring.");
    }
}
