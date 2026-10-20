using System.Reflection;

namespace ClinicSaaS.Domain.Authorization;

/// <summary>
/// The permission catalog. Permissions are fixed in code (endpoints depend on them); which role holds which
/// permission is per-tenant data.
/// </summary>
public static class Permissions
{
    public static class Patients
    {
        public const string View = "Patients.View";
        public const string Create = "Patients.Create";
        public const string Edit = "Patients.Edit";
        public const string Delete = "Patients.Delete";
        public const string Export = "Patients.Export";
    }

    /// <summary>Sensitive clinical data: allergies, chronic conditions, medical notes, visit content.</summary>
    public static class MedicalRecords
    {
        public const string View = "MedicalRecords.View";
        public const string Edit = "MedicalRecords.Edit";
        /// <summary>
        /// A user who is a doctor in the clinic (has a doctor profile) normally sees and creates only their OWN visits
        /// and prescriptions; this permission lifts that to every doctor's records. Irrelevant for non-doctors.
        /// </summary>
        public const string ViewAllDoctors = "MedicalRecords.ViewAllDoctors";
    }

    public static class Doctors
    {
        public const string View = "Doctors.View";
        public const string Manage = "Doctors.Manage";
    }

    public static class Appointments
    {
        public const string View = "Appointments.View";
        public const string Create = "Appointments.Create";
        public const string Edit = "Appointments.Edit";
        public const string Cancel = "Appointments.Cancel";
        public const string CheckIn = "Appointments.CheckIn";
    }

    public static class Visits
    {
        public const string View = "Visits.View";
        public const string Create = "Visits.Create";
        public const string Edit = "Visits.Edit";
    }

    public static class Prescriptions
    {
        public const string View = "Prescriptions.View";
        public const string Create = "Prescriptions.Create";
    }

    public static class Invoices
    {
        public const string View = "Invoices.View";
        public const string Create = "Invoices.Create";
        public const string Void = "Invoices.Void";
        public const string Refund = "Invoices.Refund";
    }

    public static class Payments
    {
        public const string Record = "Payments.Record";
    }

    public static class Reports
    {
        public const string View = "Reports.View";
        public const string Financial = "Reports.Financial";
    }

    public static class Staff
    {
        public const string View = "Staff.View";
        public const string Manage = "Staff.Manage";
    }

    public static class Roles
    {
        public const string Manage = "Roles.Manage";
    }

    public static class Branches
    {
        public const string Manage = "Branches.Manage";
    }

    public static class Settings
    {
        public const string Manage = "Settings.Manage";
    }

    public static class Subscription
    {
        public const string View = "Subscription.View";
        public const string Manage = "Subscription.Manage";
    }

    public static class AuditLogs
    {
        public const string View = "AuditLogs.View";
    }

    public static class Files
    {
        public const string View = "Files.View";
        public const string Upload = "Files.Upload";
    }

    /// <summary>Platform (super admin) permissions — never granted through tenant roles.</summary>
    public static class Platform
    {
        public const string TenantsView = "Platform.Tenants.View";
        public const string TenantsManage = "Platform.Tenants.Manage";
        public const string PlansManage = "Platform.Plans.Manage";
        public const string BillingView = "Platform.Billing.View";
        public const string BillingManage = "Platform.Billing.Manage";
        public const string AuditLogsView = "Platform.AuditLogs.View";
        public const string SystemHealthView = "Platform.SystemHealth.View";
        public const string FeatureFlagsManage = "Platform.FeatureFlags.Manage";
    }

    /// <summary>All tenant-level permissions (excludes Platform.*).</summary>
    public static readonly IReadOnlyList<string> AllTenant = Collect(typeof(Permissions), excludePlatform: true);

    public static readonly IReadOnlyList<string> AllPlatform = Collect(typeof(Platform), excludePlatform: false);

    public static bool IsTenantPermission(string permission) => AllTenant.Contains(permission);

    private static List<string> Collect(Type root, bool excludePlatform)
    {
        var result = new List<string>();
        foreach (var nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
        {
            if (excludePlatform && nested == typeof(Platform)) continue;
            result.AddRange(Collect(nested, excludePlatform));
        }
        result.AddRange(root.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!));
        return result;
    }
}
