using ClinicSaaS.Application.Appointments;
using ClinicSaaS.Application.Attachments;
using ClinicSaaS.Application.Auth;
using ClinicSaaS.Application.Common;
using ClinicSaaS.Application.Doctors;
using ClinicSaaS.Application.Organization;
using ClinicSaaS.Application.Patients;
using ClinicSaaS.Application.Platform;
using ClinicSaaS.Application.Prescriptions;
using ClinicSaaS.Application.Saas;
using ClinicSaaS.Application.Staff;
using ClinicSaaS.Application.Tenancy;
using ClinicSaaS.Application.Visits;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicSaaS.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration config)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, ServiceLifetime.Scoped);

        services.Configure<AuthOptions>(config.GetSection(AuthOptions.Section));
        services.Configure<SaasOptions>(config.GetSection(SaasOptions.Section));
        services.Configure<FeatureFlagOptions>(config.GetSection(FeatureFlagOptions.Section));
        services.Configure<BootstrapOptions>(config.GetSection(BootstrapOptions.Section));

        services.AddScoped<ClinicContext>();
        services.AddScoped<AuthService>();
        services.AddScoped<MfaService>();
        services.AddScoped<TenantProvisioningService>();
        services.AddScoped<CurrentPlanResolver>();
        services.AddScoped<IFeatureGate, FeatureGate>();
        services.AddScoped<IUsageLimiter, UsageLimiter>();
        services.AddScoped<SubscriptionQueries>();
        services.AddScoped<AuditLogQueries>();

        services.AddScoped<ClinicSettingsService>();
        services.AddScoped<BranchService>();
        services.AddScoped<RoleService>();
        services.AddScoped<StaffService>();
        services.AddScoped<DoctorService>();
        services.AddScoped<ClinicServiceCatalog>();
        services.AddScoped<PatientService>();
        services.AddScoped<AppointmentService>();
        services.AddScoped<VisitService>();
        services.AddScoped<FollowUpQueries>();
        services.AddScoped<PrescriptionService>();
        services.AddScoped<AttachmentService>();

        services.AddScoped<PlatformTenantService>();
        services.AddScoped<PlatformSubscriptionService>();
        services.AddScoped<TenantAccessResolver>();
        services.AddScoped<PlatformBootstrap>();
        return services;
    }
}
