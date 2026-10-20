using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClinicSaaS.Application.Platform;

public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";
    /// <summary>Email of the first super admin. With <see cref="SuperAdminPassword"/>, both come from the secret store.</summary>
    public string? SuperAdminEmail { get; set; }
    public string? SuperAdminPassword { get; set; }
}

/// <summary>
/// Creates the first platform super admin on startup when configured and absent. Idempotent; never changes an
/// existing account. Remove the secrets from the environment once the account exists.
/// </summary>
public sealed class PlatformBootstrap(IAppDbContext db, IPasswordHasher hasher, ILogger<PlatformBootstrap> logger)
{
    public async Task EnsureSuperAdminAsync(BootstrapOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SuperAdminEmail) || string.IsNullOrWhiteSpace(options.SuperAdminPassword)) return;
        if (options.SuperAdminPassword.Length < 12) throw new InvalidOperationException("Bootstrap:SuperAdminPassword must be at least 12 characters.");

        var normalized = User.Normalize(options.SuperAdminEmail);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct))
        {
            // P3-SEC-17: the secrets only matter for the very first start; left in the environment, a platform password
            // stays readable by anyone who can read the process configuration.
            logger.LogWarning(SecurityLogEvents.BootstrapSecretStillConfigured,
                "Bootstrap:SuperAdminPassword is still configured although the account exists; remove the Bootstrap secrets from the environment.");
            return;
        }

        var user = new User
        {
            Email = options.SuperAdminEmail.Trim(),
            NormalizedEmail = normalized,
            FullName = "Platform Admin",
            EmailConfirmed = true,
            PlatformRole = PlatformRole.SuperAdmin,
        };
        user.PasswordHash = hasher.Hash(user, options.SuperAdminPassword);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Bootstrap super admin account created; remove Bootstrap secrets from the environment.");
    }
}
