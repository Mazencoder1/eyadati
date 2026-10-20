using System.Reflection;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>How strictly this process must treat its configuration.</summary>
public enum RuntimeProfile
{
    /// <summary>A developer machine: convenient defaults (ephemeral JWT key, dev mailbox, auto-migrate).</summary>
    Development,

    /// <summary>The automated test host (WebApplicationFactory). Never a deployed process.</summary>
    AutomatedTest,

    /// <summary>Everything else — Production, Staging, any custom name. Fails closed on unsafe configuration.</summary>
    ProductionLike,
}

public static class RuntimeProfiles
{
    /// <summary>
    /// Only <c>Development</c> gets development conveniences. <c>Testing</c> is honoured only when the API is hosted
    /// by a test process; if the API itself is the executable, "Testing" is just another deployed environment, so a
    /// deployment cannot switch the guards off by naming its environment "Testing".
    /// </summary>
    public static RuntimeProfile Of(IHostEnvironment environment) => Of(environment, Assembly.GetEntryAssembly());

    /// <summary>Overload with an explicit entry assembly, so the rule can be tested.</summary>
    public static RuntimeProfile Of(IHostEnvironment environment, Assembly? entryAssembly)
    {
        if (environment.IsDevelopment()) return RuntimeProfile.Development;
        var apiIsTheExecutable = entryAssembly is not null && entryAssembly == typeof(RuntimeProfiles).Assembly;
        if (environment.IsEnvironment("Testing") && !apiIsTheExecutable) return RuntimeProfile.AutomatedTest;
        return RuntimeProfile.ProductionLike;
    }
}
