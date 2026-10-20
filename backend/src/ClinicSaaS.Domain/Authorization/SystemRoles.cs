using ClinicSaaS.Domain.Identity;
using P = ClinicSaaS.Domain.Authorization.Permissions;

namespace ClinicSaaS.Domain.Authorization;

/// <summary>Role templates copied into every new clinic. Clinics may later edit their copies.</summary>
public static class SystemRoles
{
    public const string TenantOwner = "tenant_owner";
    public const string ClinicManager = "clinic_manager";
    public const string Doctor = "doctor";
    public const string Nurse = "nurse";
    public const string Receptionist = "receptionist";
    public const string Accountant = "accountant";
    public const string Staff = "staff";

    public sealed record Template(string Key, string NameAr, string NameEn, IReadOnlyList<string> Permissions);

    public static readonly IReadOnlyList<Template> Templates =
    [
        new(TenantOwner, "مالك العيادة", "Clinic owner", P.AllTenant),
        new(ClinicManager, "مدير العيادة", "Clinic manager",
            P.AllTenant.Where(p => p != P.Subscription.Manage && p != P.Roles.Manage).ToList()),
        new(Doctor, "طبيب", "Doctor",
        [
            P.Patients.View, P.Patients.Create, P.Patients.Edit,
            P.MedicalRecords.View, P.MedicalRecords.Edit,
            P.Doctors.View,
            P.Appointments.View, P.Appointments.Edit, P.Appointments.CheckIn,
            P.Visits.View, P.Visits.Create, P.Visits.Edit,
            P.Prescriptions.View, P.Prescriptions.Create,
            P.Files.View, P.Files.Upload,
        ]),
        new(Nurse, "تمريض", "Nurse",
        [
            P.Patients.View, P.Patients.Edit,
            P.MedicalRecords.View,
            P.Doctors.View,
            P.Appointments.View, P.Appointments.CheckIn,
            P.Visits.View,
            P.Prescriptions.View,
            P.Files.View, P.Files.Upload,
        ]),
        // Reception books and bills but never reads clinical content (data minimization).
        new(Receptionist, "موظف استقبال", "Receptionist",
        [
            P.Patients.View, P.Patients.Create, P.Patients.Edit,
            P.Doctors.View,
            P.Appointments.View, P.Appointments.Create, P.Appointments.Edit, P.Appointments.Cancel, P.Appointments.CheckIn,
            P.Invoices.View, P.Invoices.Create, P.Payments.Record,
        ]),
        new(Accountant, "محاسب", "Accountant",
        [
            P.Patients.View,
            P.Invoices.View, P.Invoices.Create, P.Invoices.Void, P.Invoices.Refund, P.Payments.Record,
            P.Reports.View, P.Reports.Financial,
            P.Subscription.View,
        ]),
        new(Staff, "موظف", "Staff", [P.Patients.View, P.Doctors.View, P.Appointments.View]),
    ];

    public static Template Get(string key) => Templates.Single(t => t.Key == key);

    /// <summary>Platform roles map to fixed platform permissions.</summary>
    public static IReadOnlyList<string> PlatformPermissions(PlatformRole role) => role switch
    {
        PlatformRole.SuperAdmin => P.AllPlatform,
        PlatformRole.Support => [P.Platform.TenantsView, P.Platform.AuditLogsView, P.Platform.SystemHealthView],
        PlatformRole.BillingAdmin => [P.Platform.TenantsView, P.Platform.BillingView, P.Platform.BillingManage],
        _ => [],
    };
}
