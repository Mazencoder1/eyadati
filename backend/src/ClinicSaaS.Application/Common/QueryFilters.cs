namespace ClinicSaaS.Application.Common;

/// <summary>
/// Names of the global query filters registered by the data layer. Code that must look across tenants
/// (login, refresh, platform admin) disables only <see cref="Tenant"/> — explicitly and visibly — and keeps
/// <see cref="SoftDelete"/> on.
/// </summary>
public static class QueryFilters
{
    public const string Tenant = "Tenant";
    public const string SoftDelete = "SoftDelete";

    public static readonly string[] TenantOnly = [Tenant];
}
