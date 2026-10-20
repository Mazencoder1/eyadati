using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClinicSaaS.Application.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ClinicSaaS.Infrastructure.Security;

public sealed class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "clinic-saas";
    public string Audience { get; set; } = "clinic-saas-web";
    /// <summary>HMAC-SHA256 key, ≥ 32 bytes. Supplied by environment / secret store — never committed.</summary>
    public string SigningKey { get; set; } = "";
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Keys that are still ACCEPTED when validating tokens but never used to sign — the previous signing key during a
    /// rotation. Remove an entry once tokens signed with it have expired (<see cref="AccessTokenMinutes"/> plus clock skew).
    /// Sessions do not depend on the key (refresh tokens are opaque and stored hashed), so even without an overlap a
    /// rotation only makes clients refresh once.
    /// </summary>
    public string[] PreviousSigningKeys { get; set; } = [];

    public SymmetricSecurityKey GetKey() => new(Encoding.UTF8.GetBytes(SigningKey));

    /// <summary>Current key first, then the previous ones.</summary>
    public IReadOnlyList<SecurityKey> GetValidationKeys() =>
        [GetKey(), .. PreviousSigningKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k)))];
}

/// <summary>Claim names in our access tokens.</summary>
public static class AppClaims
{
    public const string UserId = JwtRegisteredClaimNames.Sub;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Name = JwtRegisteredClaimNames.Name;
    public const string TenantId = "tid";
    public const string MembershipId = "mid";
    public const string PlatformRole = "prole";
    public const string Permission = "perm";
    public const string Branch = "br";
    public const string SessionId = "sid";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, IClock clock) : ITokenService
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateAccessToken(TokenClaims c)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var expires = now.AddMinutes(o.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(AppClaims.UserId, c.UserId.ToString()),
            new(AppClaims.Email, c.Email),
            new(AppClaims.Name, c.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(AppClaims.PlatformRole, c.PlatformRole.ToString()),
            new(AppClaims.SessionId, c.SessionId.ToString()),
        };
        if (c.TenantId is { } tid) claims.Add(new Claim(AppClaims.TenantId, tid.ToString()));
        if (c.MembershipId is { } mid) claims.Add(new Claim(AppClaims.MembershipId, mid.ToString()));
        claims.AddRange(c.Permissions.Select(p => new Claim(AppClaims.Permission, p)));
        claims.AddRange(c.BranchIds.Select(b => new Claim(AppClaims.Branch, b.ToString())));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(o.GetKey(), SecurityAlgorithms.HmacSha256),
        });
        return new AccessToken(token, expires);
    }

    public string CreateOpaqueToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public string Hash(string opaqueToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(opaqueToken))).ToLowerInvariant();
}
