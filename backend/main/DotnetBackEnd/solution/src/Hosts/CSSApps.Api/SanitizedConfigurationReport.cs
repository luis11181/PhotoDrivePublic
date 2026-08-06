using System.Text;
using CSSApps.Api.AccessControl;
using Microsoft.Data.Sqlite;

namespace CSSApps.Api;

/// <summary>Operator-visible effective configuration with sensitive values omitted by construction.</summary>
public sealed record SanitizedConfigurationReport(
    string Environment,
    IReadOnlyList<string> ConfigurationLayers,
    string ListeningAddress,
    IReadOnlyList<string> AllowedHosts,
    IReadOnlyList<string> FrontendOrigins,
    IReadOnlyList<string> FrontendHttpHostsAllowedOnAnyPort,
    bool HttpsRequired,
    bool ReverseProxyEnabled,
    int KnownProxyAddressCount,
    int KnownProxyNetworkCount,
    bool IpAllowlistEnabled,
    int AllowedClientNetworkCount,
    string UsersDatabaseDirectory,
    string CatalogDatabaseDirectory,
    string DataProtectionKeysDirectory,
    string StagingDirectory,
    string ThumbnailDirectory,
    string OriginalCacheDirectory,
    string BackupDirectory,
    bool ScheduledBackupsEnabled,
    string BackupAnchorLocalTime,
    int BackupIntervalHours,
    string BackupTimeZone)
{
    public static SanitizedConfigurationReport Create(
        IConfiguration configuration,
        string environmentName,
        string contentRootPath)
    {
        var layers = new List<string>();
        AddFileIfPresent(layers, contentRootPath, "appsettings.json");
        layers.Add("environment variables");

        return new SanitizedConfigurationReport(
            environmentName,
            layers,
            configuration["Urls"] ?? "(not configured)",
            Split(configuration["AllowedHosts"], ';'),
            configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? [],
            FrontendOriginPolicy.Read(configuration, FrontendOriginPolicy.AllowedHttpHostsAnyPortKey),
            configuration.GetValue<bool?>("Security:RequireHttps") ??
                !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase),
            configuration.GetValue<bool>("ReverseProxy:Enabled"),
            ConfigurationList.Read(configuration, "ReverseProxy:KnownProxies").Length,
            ConfigurationList.Read(configuration, "ReverseProxy:KnownNetworks").Length,
            configuration.GetValue<bool?>("AccessControl:IpAllowlist:Enabled") ??
                !string.Equals(environmentName, Environments.Development, StringComparison.OrdinalIgnoreCase),
            ConfigurationList.Read(configuration, "AccessControl:IpAllowlist:AllowedNetworks").Length,
            ConnectionDirectory(configuration, "UsersModule"),
            ConnectionDirectory(configuration, "MediaCatalog"),
            DirectoryValue(configuration, "Security:DataProtectionKeysDirectory"),
            DirectoryValue(configuration, "MediaStorage:LocalDisk:StagingDirectory"),
            DirectoryValue(configuration, "MediaStorage:LocalDisk:ThumbnailDirectory"),
            DirectoryValue(configuration, "MediaStorage:LocalDisk:OriginalCacheDirectory"),
            DirectoryValue(configuration, "MediaStorage:LocalDisk:BackupDirectory"),
            configuration.GetValue<bool?>("MediaStorage:Backups:ScheduleEnabled") ?? true,
            configuration["MediaStorage:Backups:AnchorLocalTime"] ?? "00:00",
            configuration.GetValue<int?>("MediaStorage:Backups:IntervalHours") ?? 12,
            configuration["MediaStorage:Backups:TimeZoneId"] ?? "UTC");
    }

    public string ToDisplayText()
    {
        var text = new StringBuilder();
        text.AppendLine("PhotoDrive effective configuration (sanitized)");
        text.AppendLine($"Environment: {Environment}");
        text.AppendLine("Configuration layers:");
        foreach (var layer in ConfigurationLayers) text.AppendLine($"- {layer}");
        text.AppendLine($"Listening address: {ListeningAddress}");
        text.AppendLine($"Allowed API hosts: {FormatValues(AllowedHosts)}");
        text.AppendLine($"Frontend origins: {FormatValues(FrontendOrigins)}");
        text.AppendLine($"Frontend HTTP hosts allowed on any port: {FormatValues(FrontendHttpHostsAllowedOnAnyPort)}");
        text.AppendLine($"HTTPS enforcement: {Status(HttpsRequired)}");
        text.AppendLine($"Reverse proxy: {Status(ReverseProxyEnabled)}; trusted addresses={KnownProxyAddressCount}; trusted networks={KnownProxyNetworkCount}");
        text.AppendLine($"Client IP allowlist: {Status(IpAllowlistEnabled)}; entries={AllowedClientNetworkCount}");
        text.AppendLine("Directories:");
        text.AppendLine($"- Users database: {UsersDatabaseDirectory}");
        text.AppendLine($"- Catalog database: {CatalogDatabaseDirectory}");
        text.AppendLine($"- Data Protection keys: {DataProtectionKeysDirectory}");
        text.AppendLine($"- Staging: {StagingDirectory}");
        text.AppendLine($"- Thumbnails: {ThumbnailDirectory}");
        text.AppendLine($"- Original cache: {OriginalCacheDirectory}");
        text.AppendLine($"- Backups: {BackupDirectory}");
        text.Append($"Backup schedule: {Status(ScheduledBackupsEnabled)}; anchor={BackupAnchorLocalTime}; intervalHours={BackupIntervalHours}; timeZone={BackupTimeZone}");
        return text.ToString();
    }

    private static void AddFileIfPresent(List<string> layers, string contentRootPath, string fileName)
    {
        if (File.Exists(Path.Combine(contentRootPath, fileName))) layers.Add(fileName);
    }

    private static string[] Split(string? value, char separator) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ConnectionDirectory(IConfiguration configuration, string connectionName)
    {
        var value = configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(value)) return "(not configured)";

        try
        {
            var dataSource = new SqliteConnectionStringBuilder(value).DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) || !Path.IsPathFullyQualified(dataSource))
                return "(not configured with an absolute path)";
            return Path.GetDirectoryName(dataSource) ?? Path.GetPathRoot(dataSource) ?? "(not configured)";
        }
        catch (ArgumentException)
        {
            return "(invalid connection string)";
        }
    }

    private static string DirectoryValue(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? "(not configured)" : configuration[key]!;

    private static string FormatValues(IEnumerable<string> values)
    {
        var materialized = values.ToArray();
        return materialized.Length == 0 ? "(none)" : string.Join(", ", materialized);
    }

    private static string Status(bool enabled) => enabled ? "enabled" : "disabled";
}
