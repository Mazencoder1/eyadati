using System.Text.Json;
using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Auditing;
using ClinicSaaS.Domain.Identity;
using ClinicSaaS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IdentityHasher = Microsoft.AspNetCore.Identity.PasswordHasher<ClinicSaaS.Domain.Identity.User>;
using IdentityVerify = Microsoft.AspNetCore.Identity.PasswordVerificationResult;

namespace ClinicSaaS.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>Request-scoped tenant scope: the user's tenant by default, overridable by explicit system scopes.</summary>
public sealed class TenantContext(ICurrentUser user) : ITenantContext
{
    private readonly Stack<Guid> _scopes = new();

    public Guid? TenantId => _scopes.Count > 0 ? _scopes.Peek() : user.TenantId;

    public IDisposable UseTenant(Guid tenantId)
    {
        _scopes.Push(tenantId);
        return new Scope(_scopes);
    }

    private sealed class Scope(Stack<Guid> scopes) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            scopes.Pop();
        }
    }
}

/// <summary>PBKDF2 (HMAC-SHA512, 100k iterations) from ASP.NET Core Identity.</summary>
public sealed class PasswordHasher : IPasswordHasher
{
    private readonly IdentityHasher _inner = new();

    public string Hash(User user, string password) => _inner.HashPassword(user, password);

    public bool Verify(User user, string hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return false;
        try
        {
            return _inner.VerifyHashedPassword(user, hash, password) is IdentityVerify.Success or IdentityVerify.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public sealed class AuditLogger(AppDbContext db, ICurrentUser user, ITenantContext tenant, IRequestContext request, IClock clock,
    ILogger<AuditLogger> logger) : IAuditLogger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Security-relevant rows waiting for their SaveChanges; mirrored to the log only once they are actually saved, so a
    // failed write never produces an alert for something that did not happen.
    private readonly List<(AuditLog Row, LogLevel Level)> _toLog = [];
    private bool _listening;

    public void Add(string action, string? entityType = null, string? entityId = null, AuditResult result = AuditResult.Success,
        object? details = null, Guid? tenantId = null, Guid? userId = null)
    {
        var json = details is null ? null : JsonSerializer.Serialize(details, Json);
        var row = new AuditLog
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Result = result,
            TenantId = tenantId ?? tenant.TenantId,
            UserId = userId ?? user.UserId,
            OccurredAtUtc = clock.UtcNow,
            IpAddress = request.IpAddress,
            UserAgent = request.UserAgent is { Length: > 256 } ua ? ua[..256] : request.UserAgent,
            CorrelationId = request.CorrelationId,
            Details = json is { Length: > 2000 } ? json[..2000] : json,
        };
        db.AuditLogs.Add(row);

        if (SecurityLogEvents.LevelFor(action) is not { } level) return;
        _toLog.Add((row, level));
        if (_listening) return;
        db.SavedChanges += LogSaved;
        _listening = true;
    }

    // Identifiers only: never Details, IP address or user agent (the audit row keeps those; AuditId links to it).
    private void LogSaved(object? sender, SavedChangesEventArgs e)
    {
        for (var i = _toLog.Count - 1; i >= 0; i--)
        {
            var (row, level) = _toLog[i];
            if (db.Entry(row).State != EntityState.Unchanged) continue;
            _toLog.RemoveAt(i);
            logger.Log(level, SecurityLogEvents.SecurityAudit,
                "Security event {AuditAction} {AuditResult} (audit {AuditId}, {AuditEntityType} {AuditEntityId}, user {AuditUserId}, tenant {AuditTenantId})",
                row.Action, row.Result, row.Id, row.EntityType, row.EntityId, row.UserId, row.TenantId);
        }
    }
}

public sealed class SequenceGenerator(AppDbContext db) : ISequenceGenerator
{
    public async Task<long> NextAsync(Guid tenantId, string sequenceName, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Sequence numbers must be taken inside a transaction to stay gap-free.");

        // Atomic increment; the row lock is held until commit so concurrent callers queue instead of colliding.
        var values = await db.Database.SqlQuery<long>($"""
            UPDATE dbo.NumberSequences SET NextValue = NextValue + 1
            OUTPUT deleted.NextValue AS [Value]
            WHERE TenantId = {tenantId} AND Name = {sequenceName}
            """).ToListAsync(ct);
        if (values.Count == 1) return values[0];

        // Sequence row missing (older tenant / new sequence): create it, starting at 1.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.NumberSequences (TenantId, Name, NextValue) VALUES ({tenantId}, {sequenceName}, 2)
            """, ct);
        return 1;
    }
}
