namespace CSSApps.Api;

/// <summary>
/// Matches credentialed browser origins against exact configured origins plus explicitly trusted
/// private HTTP hostnames whose frontend port may change between runs.
/// </summary>
public sealed class FrontendOriginPolicy
{
    public const string AllowedOriginsKey = "Frontend:AllowedOrigins";
    public const string AllowedHttpHostsAnyPortKey = "Frontend:AllowedHttpHostsAnyPort";

    private readonly HashSet<string> _allowedOrigins;
    private readonly HashSet<string> _allowedHttpHostsAnyPort;

    public FrontendOriginPolicy(IConfiguration configuration)
    {
        _allowedOrigins = Read(configuration, AllowedOriginsKey)
            .Select(NormalizeOrigin)
            .Where(origin => origin is not null)
            .Select(origin => origin!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _allowedHttpHostsAnyPort = Read(configuration, AllowedHttpHostsAnyPortKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsAllowed(string origin)
    {
        if (!TryParseOrigin(origin, out var uri, out var normalized)) return false;
        if (_allowedOrigins.Contains(normalized)) return true;

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               _allowedHttpHostsAnyPort.Contains(uri.Host);
    }

    public static string[] Read(IConfiguration configuration, string key) =>
        configuration.GetSection(key).Get<string[]>()?
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

    private static string? NormalizeOrigin(string value) =>
        TryParseOrigin(value, out _, out var normalized) ? normalized : null;

    private static bool TryParseOrigin(string value, out Uri uri, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) ||
            !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
