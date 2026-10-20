using System.Security.Claims;
using System.Threading.RateLimiting;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Infrastructure.Security;

namespace ClinicSaaS.Api.Infrastructure;

public static class RateLimitPolicies
{
    /// <summary>Login, registration, tenant switch: per client IP (credential guessing).</summary>
    public const string Auth = "auth";
    /// <summary>
    /// Session refresh: per client IP, more generous than <see cref="Auth"/>. A web app refreshes on every page load
    /// (the access token lives only in memory), and a clinic's staff often share one public IP. Guessing a 256-bit
    /// refresh token is infeasible, so this budget only bounds load; reuse detection protects stolen tokens.
    /// </summary>
    public const string Refresh = "refresh";
    /// <summary>Password reset, verification emails, OTP: tight per-IP budget (each call can send a message).</summary>
    public const string Sensitive = "sensitive";

    /// <summary>
    /// In-process limiter (per instance), so production runs one API instance (owner decision 3A-D7); a shared limiter
    /// is plan task 3F, decided when scaling out. Per-account lockout already protects credentials across instances via
    /// the database.
    /// </summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var authPerMinute = config.GetValue("RateLimiting:AuthPerMinute", 10);
        var refreshPerMinute = config.GetValue("RateLimiting:RefreshPerMinute", 120);
        var sensitivePer15Min = config.GetValue("RateLimiting:SensitivePer15Minutes", 5);
        var apiPerMinute = config.GetValue("RateLimiting:ApiPerMinute", 300);

        services.AddRateLimiter(o =>
        {
            o.AddPolicy(Auth, http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(Refresh, http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = refreshPerMinute, Window = TimeSpan.FromMinutes(1) }));

            o.AddPolicy(Sensitive, http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = sensitivePer15Min, Window = TimeSpan.FromMinutes(15) }));

            // Global budget per user (authenticated) or per IP (anonymous).
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            {
                var key = http.User.FindFirstValue(AppClaims.UserId) is { } uid ? "u:" + uid : "ip:" + ClientIp(http);
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = apiPerMinute,
                    TokensPerPeriod = apiPerMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                    AutoReplenishment = true,
                });
            });

            o.OnRejected = async (ctx, ct) =>
            {
                var http = ctx.HttpContext;
                var request = http.RequestServices.GetRequiredService<IRequestContext>();
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    http.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                // Monitoring hook (brute force, abuse, or a clinic outgrowing its budget). Path only: query strings can
                // carry personal data; the client address is deliberately not logged (edge/WAF logs hold it).
                var policy = http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName ?? "global";
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RateLimitPolicies).FullName!)
                    .LogWarning(SecurityLogEvents.RateLimited, "Rate limit {RateLimitPolicy} refused {RequestMethod} {RequestPath}",
                        policy, http.Request.Method, http.Request.Path.Value);
                await http.Response.WriteAsJsonAsync(new ApiError(ErrorCodes.RateLimited,
                    ErrorCatalog.Message(ErrorCodes.RateLimited, request.Language), request.CorrelationId), ct);
            };
        });
        return services;
    }

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
