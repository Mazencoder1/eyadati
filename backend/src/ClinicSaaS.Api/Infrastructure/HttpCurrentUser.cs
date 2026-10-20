using System.Security.Claims;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Infrastructure.Security;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>The caller as described by the validated access token — the only source of tenant identity.</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    private IReadOnlySet<string>? _permissions;
    private IReadOnlyList<Guid>? _branches;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid? UserId => GuidClaim(AppClaims.UserId);
    public Guid? TenantId => GuidClaim(AppClaims.TenantId);
    public Guid? MembershipId => GuidClaim(AppClaims.MembershipId);
    public Guid? SessionId => GuidClaim(AppClaims.SessionId);

    public PlatformRole PlatformRole =>
        Enum.TryParse<PlatformRole>(Principal?.FindFirstValue(AppClaims.PlatformRole), out var r) ? r : PlatformRole.None;

    public IReadOnlySet<string> Permissions => _permissions ??=
        IsAuthenticated ? Principal!.FindAll(AppClaims.Permission).Select(c => c.Value).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    public IReadOnlyList<Guid> BranchIds => _branches ??=
        IsAuthenticated ? Principal!.FindAll(AppClaims.Branch).Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList() : [];

    public bool HasPermission(string permission) => Permissions.Contains(permission);

    private Guid? GuidClaim(string type) =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(type), out var g) ? g : null;
}

public sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private HttpContext? Http => accessor.HttpContext;

    public string? IpAddress => Http?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent => Http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;
    public string CorrelationId => Http?.TraceIdentifier ?? "none";

    public string Language
    {
        get
        {
            var header = Http?.Request.Headers.AcceptLanguage.ToString();
            return header is { Length: > 0 } && header.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "ar";
        }
    }
}
