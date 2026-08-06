using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace CSSApps.Api.AccessControl;

/// <summary>Applies the installation-wide exact-IP/CIDR allowlist.</summary>
public sealed class ClientIpAccessPolicy
{
    public const string SectionName = "AccessControl:IpAllowlist";

    private readonly IReadOnlyList<IpNetworkRule> _allowedNetworks;

    public ClientIpAccessPolicy(IConfiguration configuration, bool defaultEnabled)
    {
        Enabled = configuration.GetValue<bool?>($"{SectionName}:Enabled") ?? defaultEnabled;
        _allowedNetworks = ConfigurationList.Read(configuration, $"{SectionName}:AllowedNetworks")
            .Select((entry, index) => IpNetworkRule.TryParse(entry, out var rule) && rule.PrefixLength > 0
                ? rule
                : throw new InvalidOperationException($"IP allowlist entry {index + 1} is not a valid non-global IP address or CIDR range."))
            .ToArray();
    }

    public bool Enabled { get; }

    public bool IsAllowed(IPAddress address)
    {
        var normalized = Normalize(address);
        return _allowedNetworks.Any(network => network.Contains(normalized));
    }

    public static IReadOnlyList<string> FindConfigurationErrors(
        IConfiguration configuration,
        bool defaultEnabled)
    {
        var errors = new List<string>();
        var enabled = configuration.GetValue<bool?>($"{SectionName}:Enabled") ?? defaultEnabled;
        var entries = ConfigurationList.Read(configuration, $"{SectionName}:AllowedNetworks");
        if (enabled && entries.Length == 0)
            errors.Add($"{SectionName}:AllowedNetworks requires at least one IP address or CIDR range when the allowlist is enabled.");

        for (var index = 0; index < entries.Length; index++)
        {
            if (!IpNetworkRule.TryParse(entries[index], out var rule) || rule.PrefixLength == 0)
                errors.Add($"{SectionName}:AllowedNetworks entry {index + 1} is invalid or permits the entire internet.");
        }

        return errors;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed record IpNetworkRule(byte[] NetworkBytes, int PrefixLength)
    {
        public static bool TryParse(string value, out IpNetworkRule rule)
        {
            rule = null!;
            var parts = value.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !IPAddress.TryParse(parts[0], out var parsedAddress)) return false;

            var address = Normalize(parsedAddress);
            var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            var prefix = maxPrefix;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > maxPrefix))
                return false;

            rule = new IpNetworkRule(address.GetAddressBytes(), prefix);
            return true;
        }

        public bool Contains(IPAddress address)
        {
            var addressBytes = address.GetAddressBytes();
            if (addressBytes.Length != NetworkBytes.Length) return false;

            var wholeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;
            for (var index = 0; index < wholeBytes; index++)
            {
                if (NetworkBytes[index] != addressBytes[index]) return false;
            }

            if (remainingBits == 0) return true;
            var mask = (byte)(0xff << (8 - remainingBits));
            return (NetworkBytes[wholeBytes] & mask) == (addressBytes[wholeBytes] & mask);
        }
    }
}

/// <summary>
/// Configures ASP.NET's standard forwarded-header middleware without trusting arbitrary peers.
/// </summary>
public static class TrustedProxyConfiguration
{
    public const string SectionName = "ReverseProxy";

    public static void Apply(IConfiguration configuration, ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedHost |
                                   ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var value in ConfigurationList.Read(configuration, $"{SectionName}:KnownProxies"))
            options.KnownProxies.Add(IPAddress.Parse(value));
        foreach (var value in ConfigurationList.Read(configuration, $"{SectionName}:KnownNetworks"))
        {
            var network = System.Net.IPNetwork.Parse(value);
            if (network.PrefixLength == 0)
                throw new InvalidOperationException("A trusted proxy network cannot permit the entire internet.");
            options.KnownIPNetworks.Add(network);
        }
    }

    public static IReadOnlyList<string> FindConfigurationErrors(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>($"{SectionName}:Enabled")) return [];

        var errors = new List<string>();
        var proxies = ConfigurationList.Read(configuration, $"{SectionName}:KnownProxies");
        var networks = ConfigurationList.Read(configuration, $"{SectionName}:KnownNetworks");
        if (proxies.Length == 0 && networks.Length == 0)
            errors.Add("ReverseProxy requires at least one KnownProxies address or KnownNetworks CIDR when enabled.");

        for (var index = 0; index < proxies.Length; index++)
        {
            if (!IPAddress.TryParse(proxies[index], out _))
                errors.Add($"ReverseProxy:KnownProxies entry {index + 1} is not a valid IP address.");
        }

        for (var index = 0; index < networks.Length; index++)
        {
            if (!System.Net.IPNetwork.TryParse(networks[index], out var network) || network.PrefixLength == 0)
                errors.Add($"ReverseProxy:KnownNetworks entry {index + 1} is invalid or trusts the entire internet.");
        }

        return errors;
    }
}

internal static class ConfigurationList
{
    public static string[] Read(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var childEntries = section.GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        // An explicit JSON array in the final local deployment file must replace a lower-priority
        // scalar environment value instead of silently adding to it.
        var entries = childEntries.Length > 0
            ? childEntries
            : section.Value?.Split(
                [',', ';'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed class ClientIpAccessMiddleware(
    RequestDelegate next,
    ClientIpAccessPolicy policy,
    ILogger<ClientIpAccessMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!policy.Enabled)
        {
            await next(context);
            return;
        }

        var clientIp = context.Connection.RemoteIpAddress;
        // Local process health probes remain usable. A proxied public request has
        // already been rewritten to the external address and does not receive this bypass.
        if (context.Request.Path.Equals("/health", StringComparison.Ordinal) &&
            clientIp is not null && IPAddress.IsLoopback(clientIp))
        {
            await next(context);
            return;
        }

        if (clientIp is not null && policy.IsAllowed(clientIp))
        {
            await next(context);
            return;
        }

        logger.LogDebug("Blocked request outside the configured client IP allowlist");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            title = "Access denied",
            status = StatusCodes.Status403Forbidden,
            detail = "This installation only accepts requests from configured client networks."
        });
    }
}
