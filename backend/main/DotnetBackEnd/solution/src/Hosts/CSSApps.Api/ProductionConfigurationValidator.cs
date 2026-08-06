using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using CSSApps.Api.AccessControl;

namespace CSSApps.Api;

/// <summary>Defines the supported, fail-closed configuration contract outside Development.</summary>
public static class ProductionConfigurationValidator
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;

        var errors = FindErrors(configuration);
        if (errors.Count == 0) return;

        throw new InvalidOperationException(
            "Production configuration is invalid:" + Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
    }

    public static IReadOnlyList<string> FindErrors(IConfiguration configuration)
    {
        var errors = new List<string>();
        var allowedHosts = (configuration["AllowedHosts"] ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowedHosts.Length == 0 || allowedHosts.Contains("*", StringComparer.Ordinal) || allowedHosts.Any(IsPlaceholder))
            errors.Add("AllowedHosts must contain the public hostname and cannot be '*' or a placeholder.");

        var requireHttps = configuration.GetValue<bool?>("Security:RequireHttps") ?? true;
        var origins = FrontendOriginPolicy.Read(configuration, FrontendOriginPolicy.AllowedOriginsKey);
        if (origins.Length == 0)
        {
            errors.Add("Frontend:AllowedOrigins must contain at least one browser origin.");
        }
        else
        {
            foreach (var origin in origins)
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    !IsAllowedOriginScheme(uri, requireHttps) ||
                    !string.Equals(uri.AbsolutePath, "/", StringComparison.Ordinal) ||
                    !string.IsNullOrEmpty(uri.Query) ||
                    !string.IsNullOrEmpty(uri.Fragment))
                {
                    errors.Add($"Frontend origin '{origin}' must be an HTTPS origin, or an explicitly enabled private HTTP origin, without a path, query, or fragment.");
                    continue;
                }

            }
        }

        var anyPortHttpHosts = FrontendOriginPolicy.Read(
            configuration,
            FrontendOriginPolicy.AllowedHttpHostsAnyPortKey);
        if (requireHttps && anyPortHttpHosts.Length > 0)
            errors.Add("Frontend:AllowedHttpHostsAnyPort requires Security:RequireHttps=false.");

        foreach (var host in anyPortHttpHosts)
        {
            if (Uri.CheckHostName(host) != UriHostNameType.Dns || !IsPrivateHost(host))
            {
                errors.Add($"Frontend:AllowedHttpHostsAnyPort entry '{host}' must be a private DNS hostname without a scheme, port, or wildcard.");
                continue;
            }

            if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Frontend:AllowedHttpHostsAnyPort entry '{host}' must also appear in AllowedHosts.");
        }

        RequireAbsoluteSqlitePath(configuration, "UsersModule", errors);
        RequireAbsoluteSqlitePath(configuration, "MediaCatalog", errors);
        RequireAbsoluteDirectory(configuration, "Security:DataProtectionKeysDirectory", errors);
        RequireAbsoluteDirectory(configuration, "MediaStorage:LocalDisk:StagingDirectory", errors);
        RequireAbsoluteDirectory(configuration, "MediaStorage:LocalDisk:ThumbnailDirectory", errors);
        RequireAbsoluteDirectory(configuration, "MediaStorage:LocalDisk:OriginalCacheDirectory", errors);
        RequireAbsoluteDirectory(configuration, "MediaStorage:LocalDisk:BackupDirectory", errors);

        RequireValue(configuration, "MediaStorage:S3:BucketName", errors);
        RequireValue(configuration, "MediaStorage:S3:ProfileName", errors);
        RequireValue(configuration, "MediaStorage:S3:Region", errors);
        RequireValue(configuration, "MediaStorage:S3Backups:BucketName", errors);
        RequireValue(configuration, "MediaStorage:S3Backups:ProfileName", errors);
        RequireValue(configuration, "MediaStorage:S3Backups:Region", errors);
        if (string.Equals(configuration["MediaStorage:S3:BucketName"], configuration["MediaStorage:S3Backups:BucketName"], StringComparison.Ordinal))
            errors.Add("The originals and backup S3 buckets must be different.");

        var ownerEmail = configuration["FamilyAuthentication:BootstrapOwner:Email"];
        if (string.IsNullOrWhiteSpace(ownerEmail) || !new EmailAddressAttribute().IsValid(ownerEmail) || IsPlaceholder(ownerEmail))
            errors.Add("FamilyAuthentication:BootstrapOwner:Email must be a valid, non-placeholder owner email.");

        var initialPassword = configuration["FamilyAuthentication:BootstrapOwner:InitialPassword"];
        if (!string.IsNullOrWhiteSpace(initialPassword) && IsPlaceholder(initialPassword))
            errors.Add("FamilyAuthentication:BootstrapOwner:InitialPassword cannot be a template placeholder.");

        RequireValue(configuration, "JwtSettings:Issuer", errors);
        RequireValue(configuration, "JwtSettings:Audience", errors);
        RequireSecret(configuration, "JwtSettings:AccessTokenSecret", errors);
        if (!configuration.GetValue<bool>("JwtSettings:ValidateIssuer") ||
            !configuration.GetValue<bool>("JwtSettings:ValidateAudience"))
            errors.Add("JWT issuer and audience validation must remain enabled in production.");

        if (!string.IsNullOrWhiteSpace(configuration["Startup:DevelopmentDatabaseInitializationMode"]))
            errors.Add("Development database initialization cannot be enabled in production.");

        errors.AddRange(ClientIpAccessPolicy.FindConfigurationErrors(configuration, defaultEnabled: true));
        errors.AddRange(TrustedProxyConfiguration.FindConfigurationErrors(configuration));

        return errors;
    }

    private static void RequireAbsoluteSqlitePath(IConfiguration configuration, string name, List<string> errors)
    {
        var value = configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"ConnectionStrings:{name} is required.");
            return;
        }

        try
        {
            var path = new SqliteConnectionStringBuilder(value).DataSource;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || IsPlaceholder(path))
                errors.Add($"ConnectionStrings:{name} must use an absolute, non-placeholder SQLite path.");
        }
        catch (ArgumentException)
        {
            errors.Add($"ConnectionStrings:{name} is not a valid SQLite connection string.");
        }
    }

    private static bool IsAllowedOriginScheme(Uri uri, bool requireHttps)
    {
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
        return !requireHttps &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               IsPrivateHost(uri.Host);
    }

    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return true;

        // Unqualified DNS names resolve through private mechanisms such as mDNS search domains
        // or Tailscale MagicDNS and cannot name a public Internet origin by themselves.
        if (!host.Contains('.') && !host.Contains(':')) return true;

        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return true;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 ||
                   bytes[0] == 127 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31;

        return address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
    }

    private static void RequireAbsoluteDirectory(IConfiguration configuration, string key, List<string> errors)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || IsPlaceholder(value))
            errors.Add($"{key} must be an absolute, non-placeholder path.");
    }

    private static void RequireValue(IConfiguration configuration, string key, List<string> errors)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value)) errors.Add($"{key} is required and cannot be a placeholder.");
    }

    private static void RequireSecret(IConfiguration configuration, string key, List<string> errors)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value) || IsPlaceholder(value) || System.Text.Encoding.UTF8.GetByteCount(value) < 32)
            errors.Add($"{key} must contain at least 32 non-placeholder UTF-8 bytes.");
    }

    private static bool IsPlaceholder(string value) =>
        value.Contains("replace-", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("/absolute/path", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("example.com", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase);
}
