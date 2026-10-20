using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using ClinicSaaS.Api.Authorization;
using ClinicSaaS.Api.Errors;
using ClinicSaaS.Api.Infrastructure;
using ClinicSaaS.Application;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Common;
using ClinicSaaS.Infrastructure;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

// The API is culture-neutral: on Saudi servers the OS culture may be ar-SA with the Hijri calendar, which would
// silently change how dates format and parse. Localization (Arabic text, Hijri display) is explicit, never ambient.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, log) =>
{
    log.ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "ClinicSaaS.Api")
        .Enrich.WithProperty("Environment", ctx.HostingEnvironment.EnvironmentName)
        .Enrich.WithProperty("Version", typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
    // Observability:ConsoleLogFormat = "json" (Staging/Production settings): one JSON object per line with every property
    // (EventId, CorrelationId, UserId, TenantId, ...), for a log platform to index and alert on. Default: readable text.
    if (string.Equals(ctx.Configuration["Observability:ConsoleLogFormat"], "json", StringComparison.OrdinalIgnoreCase))
        log.WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter(renderMessage: true));
    else
        log.WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {{cid={CorrelationId} uid={UserId} tid={TenantId}}}{NewLine}{Exception}");
    // Extra sinks registered in DI (the automated tests capture log output through this to prove nothing sensitive is logged).
    foreach (var sink in services.GetServices<Serilog.Core.ILogEventSink>()) log.WriteTo.Sink(sink);
});

// ---------------------------------------------------------------- configuration guards
// Development gets conveniences; the automated test host gets what the tests configure; EVERYTHING else (Production,
// Staging, any custom name — and "Testing" when the API itself is the executable) must be configured safely or the API
// does not start. See ProductionConfigurationGuard for the rules.
var profile = RuntimeProfiles.Of(builder.Environment);
if (profile == RuntimeProfile.ProductionLike)
    ProductionConfigurationGuard.EnforceOrThrow(builder.Configuration, builder.Environment.EnvironmentName);

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
{
    if (profile != RuntimeProfile.Development)
        throw new InvalidOperationException("Jwt:SigningKey must be supplied via environment/secret store outside Development.");
    // Development convenience: an ephemeral key (sessions end on restart). Never used outside Development.
    jwt.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    builder.Configuration[$"{JwtOptions.Section}:SigningKey"] = jwt.SigningKey;
}
if (jwt.GetKey().KeySize < 256)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");

// ---------------------------------------------------------------- services
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
// Encrypts the platform admins' TOTP secrets and signs nothing else. Production-like environments must supply a
// persisted key ring protected by a certificate (see MfaDataProtection); otherwise the API does not start.
builder.Services.AddMfaDataProtection(builder.Configuration, profile);

builder.Services.AddControllers(o => o.Filters.Add(new ProducesAttribute("application/json")))
    .AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    })
    .ConfigureApiBehaviorOptions(o =>
    {
        // Model-binding errors (malformed JSON, wrong types) use the same unified body as everything else.
        o.InvalidModelStateResponseFactory = ctx =>
        {
            var request = ctx.HttpContext.RequestServices.GetRequiredService<IRequestContext>();
            // "$" (unparseable body) has no field name left after trimming; keys that normalize alike are merged.
            static string Field(string key) => key.TrimStart('$', '.') is { Length: > 0 } k ? char.ToLowerInvariant(k[0]) + k[1..] : "body";
            var errors = ctx.ModelState.Where(e => e.Value?.Errors.Count > 0)
                .Select(e => Field(e.Key)).Distinct()
                .ToDictionary(k => k, _ => new[] { ErrorCatalog.Message("invalid_value", request.Language) });
            return new BadRequestObjectResult(new ApiError(ErrorCodes.Validation,
                ErrorCatalog.Message(ErrorCodes.Validation, request.Language), request.CorrelationId, errors));
        };
    });

// Same JSON shape for bodies written outside MVC (exception handler, 401/403, 429).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        // Current key first; previous keys (rotation overlap) only validate, never sign.
        IssuerSigningKeys = jwt.GetValidationKeys(),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = AppClaims.Name,
    };
});
builder.Services.AddAuthorization(o =>
{
    // Secure by default: every endpoint requires authentication unless it opts out with [AllowAnonymous].
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.AddPolicy(TenantMemberAttribute.TenantMemberPolicy, p => p.RequireAuthenticatedUser().RequireClaim(AppClaims.TenantId));
});
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();

builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddOutputCache();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowCredentials()
    .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
    // No X-Client-Type: native clients are not browsers, so a browser has no business sending it (see NativeClient).
    .WithHeaders("Authorization", "Content-Type", "Accept-Language", "X-Correlation-Id")
    .WithExposedHeaders("X-Correlation-Id", "Retry-After")));

// Trust X-Forwarded-* only from the proxies/networks listed in configuration (see ForwardedHeadersSettings). Invalid
// entries were already rejected by the guard in production-like environments; elsewhere they are simply not applied.
var forwarded = ForwardedHeadersSettings.Read(builder.Configuration, new List<string>());
builder.Services.Configure<ForwardedHeadersOptions>(o => forwarded.Apply(o, profile));
builder.Services.AddHsts(o =>
{
    // Same policy as the web app (next.config.ts): two years, subdomains included. No preload.
    o.MaxAge = TimeSpan.FromDays(730);
    o.IncludeSubDomains = true;
    o.Preload = false;
});

builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("database", tags: ["ready"]);
builder.Services.AddOpenApi();

// ---------------------------------------------------------------- pipeline
var app = builder.Build();

// Email (Gate C, GD-05): the development mailbox writes password-reset, invitation and verification links to files on
// the server, so it is allowed in Development only — not in Testing (the automated tests replace the sender), Staging,
// Production or any custom environment name. Outside Development/Testing a real provider is also required; it is
// chosen in the production phase, and until then the API refuses to start there instead of losing or exposing links.
if (profile != RuntimeProfile.Development)
{
    var sender = app.Services.GetRequiredService<ClinicSaaS.Application.Abstractions.IEmailSender>();
    if (sender is ClinicSaaS.Infrastructure.Notifications.DevMailboxEmailSender)
        throw new InvalidOperationException(
            $"The DevMailbox email provider writes one-time links to disk and is only allowed in Development (current: {app.Environment.EnvironmentName}).");
    if (sender is ClinicSaaS.Infrastructure.Notifications.NotConfiguredEmailSender && profile != RuntimeProfile.AutomatedTest)
        throw new InvalidOperationException(
            $"No email provider is configured (Email:Provider) for {app.Environment.EnvironmentName}. Invitations, email verification and password reset cannot work without one.");
}

// Attachments (P1-02): local-disk storage is for Development and the automated tests only. Every other environment
// (Staging, Production, any custom name) needs an approved storage provider and malware scanner first; until then the
// API refuses to start there rather than keep patient files on a server's local disk.
if (profile == RuntimeProfile.ProductionLike
    && app.Services.GetRequiredService<ClinicSaaS.Application.Abstractions.IFileStorage>() is ClinicSaaS.Infrastructure.Storage.LocalDiskFileStorage)
    throw new InvalidOperationException(
        $"Local file storage is only allowed in Development and Testing (current: {app.Environment.EnvironmentName}). Configure an approved storage provider.");

// Dev/test convenience only: production applies reviewed, idempotent migration scripts (see backend/README.md).
// First platform super admin from the secret store (no-op unless Bootstrap:* is configured).
using (var scope = app.Services.CreateScope())
{
    if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        if (profile == RuntimeProfile.ProductionLike) throw new InvalidOperationException("Database:MigrateOnStartup is not allowed in Production.");
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }
    var bootstrap = builder.Configuration.GetSection(ClinicSaaS.Application.Platform.BootstrapOptions.Section)
        .Get<ClinicSaaS.Application.Platform.BootstrapOptions>() ?? new();
    await scope.ServiceProvider.GetRequiredService<ClinicSaaS.Application.Platform.PlatformBootstrap>().EnsureSuperAdminAsync(bootstrap, CancellationToken.None);
}

// NoProxy: the API is reached directly, so forwarded headers are not read at all (a client could forge them).
if (!forwarded.NoProxy) app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
// Outside the exception handler, so it logs the final status code (handled 4xx are not errors).
app.UseSerilogRequestLogging(o => o.GetLevel = (http, _, ex) =>
    ex is not null || http.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Information);
app.UseExceptionHandler();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<LogEnrichmentMiddleware>();
app.UseMiddleware<TenantAccessMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();
app.UseOutputCache();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();
if (app.Environment.IsDevelopment()) app.MapOpenApi().AllowAnonymous();

app.Run();

public partial class Program;
