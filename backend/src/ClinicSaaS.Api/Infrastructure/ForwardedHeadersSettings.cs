using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace ClinicSaaS.Api.Infrastructure;

/// <summary>
/// Which reverse proxies / load balancers the API trusts to tell it the real client address
/// (<c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>). Rate limiting, lockout diagnostics and the audit log all use the
/// address the framework resolves, so trusting the wrong sender lets any client choose its own address.
/// Configuration (nothing provider-specific is assumed):
/// <list type="bullet">
/// <item><c>ForwardedHeaders:KnownProxies</c> — exact proxy addresses;</item>
/// <item><c>ForwardedHeaders:KnownNetworks</c> — CIDR ranges, for load balancers whose addresses change within a subnet;</item>
/// <item><c>ForwardedHeaders:ForwardLimit</c> — how many trusted hops append to the header (default 1: only the entry
///   appended by the closest trusted proxy is used, never anything the client wrote);</item>
/// <item><c>ForwardedHeaders:NoProxy=true</c> — the API is reached directly; forwarded headers are ignored entirely.</item>
/// </list>
/// Headers are honoured only when the TCP peer is itself listed. Production-like environments must configure exactly one
/// of the two modes (see <see cref="ProductionConfigurationGuard"/>); Development keeps the framework's loopback trust.
/// </summary>
public sealed record ForwardedHeadersSettings(
    bool NoProxy, IReadOnlyList<IPAddress> Proxies, IReadOnlyList<IPNetwork> Networks, int ForwardLimit)
{
    public const string Section = "ForwardedHeaders";
    public const int MinIpv4Prefix = 8;
    public const int MinIpv6Prefix = 32;

    public bool HasTrustList => Proxies.Count > 0 || Networks.Count > 0;

    /// <summary>Reads the section; every unusable entry is reported by key (never by value) in <paramref name="problems"/>.</summary>
    public static ForwardedHeadersSettings Read(IConfiguration config, ICollection<string> problems)
    {
        var section = config.GetSection(Section);
        var proxies = new List<IPAddress>();
        foreach (var raw in section.GetSection("KnownProxies").Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(raw, out var ip) || IPAddress.Any.Equals(ip) || IPAddress.IPv6Any.Equals(ip))
                problems.Add($"{Section}:KnownProxies: an entry is not a specific IP address.");
            else
                proxies.Add(ip);
        }

        var networks = new List<IPNetwork>();
        foreach (var raw in section.GetSection("KnownNetworks").Get<string[]>() ?? [])
        {
            if (!IPNetwork.TryParse(raw, out var net) || !raw.Contains('/'))
            {
                problems.Add($"{Section}:KnownNetworks: an entry is not a CIDR range such as 10.0.0.0/24.");
                continue;
            }
            var min = net.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 ? MinIpv6Prefix : MinIpv4Prefix;
            if (net.PrefixLength < min)
                problems.Add($"{Section}:KnownNetworks: an entry is too broad to be a proxy range (prefix shorter than /{min}).");
            else
                networks.Add(net);
        }

        var limit = section.GetValue("ForwardLimit", 1);
        if (limit is < 1 or > 5) problems.Add($"{Section}:ForwardLimit: must be between 1 and 5.");
        limit = Math.Clamp(limit, 1, 5);

        return new ForwardedHeadersSettings(section.GetValue("NoProxy", false), proxies, networks, limit);
    }

    /// <summary>
    /// Applies the settings. With a production-like profile only the configured senders are trusted (the framework's
    /// default loopback trust is removed); in Development/tests the default stays, which is convenient and harmless there.
    /// </summary>
    public void Apply(ForwardedHeadersOptions options, RuntimeProfile profile)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = ForwardLimit;
        // A request whose peer is not trusted keeps its original addresses; it is not rejected.
        if (profile == RuntimeProfile.ProductionLike)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
        }
        foreach (var proxy in Proxies) options.KnownProxies.Add(proxy);
        foreach (var network in Networks) options.KnownIPNetworks.Add(network);
    }
}
