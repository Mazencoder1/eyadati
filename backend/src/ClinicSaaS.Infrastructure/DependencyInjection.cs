using ClinicSaaS.Application.Abstractions;
using ClinicSaaS.Infrastructure.Notifications;
using ClinicSaaS.Infrastructure.Persistence;
using ClinicSaaS.Infrastructure.Security;
using ClinicSaaS.Infrastructure.Services;
using ClinicSaaS.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        // Transient-fault handling is deliberately narrow (see SqlResilience): connection opens and plain reads outside a
        // transaction may be retried; writes and transactions never are. There is no EnableRetryOnFailure — EF's
        // execution strategy would re-run whole units of work, which is not safe for bookings and prescriptions.
        services.Configure<SqlResilienceOptions>(config.GetSection(SqlResilienceOptions.Section));
        services.AddSingleton<SqlResilience>();
        services.AddSingleton<SqlConnectionRetryInterceptor>();
        services.AddSingleton<SqlReadRetryInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                sql.CommandTimeout(30);
            })
            .AddInterceptors(sp.GetRequiredService<SqlConnectionRetryInterceptor>(), sp.GetRequiredService<SqlReadRetryInterceptor>()));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<ISequenceGenerator, SequenceGenerator>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.AddSingleton<ITokenService, JwtTokenService>();

        // Email: the development mailbox (files on disk) only when explicitly selected; the API refuses to start with
        // it — or with no provider at all — outside Development/Testing (Program.cs).
        services.Configure<DevMailboxOptions>(config.GetSection(DevMailboxOptions.Section));
        if (string.Equals(config[$"{EmailOptions.Section}:Provider"], EmailOptions.DevMailboxProvider, StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IEmailSender, DevMailboxEmailSender>();
        else
            services.AddSingleton<IEmailSender, NotConfiguredEmailSender>();

        // Attachments: local disk only until a production provider (and malware scanning) is approved.
        services.Configure<LocalFileStorageOptions>(config.GetSection(LocalFileStorageOptions.Section));
        services.AddSingleton<IFileStorage, LocalDiskFileStorage>();
        return services;
    }
}
