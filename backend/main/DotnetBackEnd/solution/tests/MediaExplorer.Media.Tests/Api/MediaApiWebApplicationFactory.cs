using CSSApps.Api;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MediaExplorer.Media.Tests.Api;

internal sealed class MediaApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"media-explorer-api-tests-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _previousEnvironment = new(StringComparer.Ordinal);

    public const string OwnerEmail = "mediaexplorer.dev@example.test";
    public const string OwnerPassword = "MediaExplorerDev!2026";
    public TestObjectStoreFactory ObjectStore { get; } = new();

    public MediaApiWebApplicationFactory()
    {
        Directory.CreateDirectory(_root);
        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
        SetEnvironment("ConnectionStrings__MediaCatalog", $"Data Source={Path.Combine(_root, "catalog.db")};Foreign Keys=True;Pooling=False");
        SetEnvironment("ConnectionStrings__UsersModule", $"Data Source={Path.Combine(_root, "users.db")};Foreign Keys=True;Pooling=False");
        SetEnvironment("Startup__DevelopmentDatabaseInitializationMode", "Migrate");
        SetEnvironment("Security__RequireHttps", "false");
        SetEnvironment("JwtSettings__AccessTokenSecret", "integration-tests-access-token-secret-key-2026");
        SetEnvironment("AccessControl__IpAllowlist__Enabled", "false");
        SetEnvironment("ReverseProxy__Enabled", "false");
        SetEnvironment("FamilyAuthentication__PasswordLoginEnabled", "true");
        SetEnvironment("FamilyAuthentication__BootstrapOwner__Email", OwnerEmail);
        SetEnvironment("FamilyAuthentication__BootstrapOwner__DisplayName", "Integration Owner");
        SetEnvironment("FamilyAuthentication__BootstrapOwner__InitialPassword", OwnerPassword);
        SetEnvironment("UserSeeding__DevelopmentDefaultUser__Enabled", "true");
        SetEnvironment("UserSeeding__DevelopmentDefaultUser__Email", OwnerEmail);
        SetEnvironment("UserSeeding__DevelopmentDefaultUser__Password", OwnerPassword);
        SetEnvironment("UserSeeding__DevelopmentDefaultUser__FirstName", "Integration");
        SetEnvironment("UserSeeding__DevelopmentDefaultUser__LastName", "Owner");
        SetEnvironment("UserSeeding__DevelopmentDefaultUser__Role", "OWNER");
        SetEnvironment("MediaStorage__Backups__ScheduleEnabled", "false");
        SetEnvironment("MediaStorage__LocalDisk__StagingDirectory", Path.Combine(_root, "staging"));
        SetEnvironment("MediaStorage__LocalDisk__ThumbnailDirectory", Path.Combine(_root, "thumbnails"));
        SetEnvironment("MediaStorage__LocalDisk__OriginalCacheDirectory", Path.Combine(_root, "cache", "blobs", "sha256"));
        SetEnvironment("MediaStorage__LocalDisk__OriginalCacheMaxBytes", (32L * 1024 * 1024).ToString());
        SetEnvironment("MediaStorage__LocalDisk__BackupDirectory", Path.Combine(_root, "backups"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MediaCatalog"] = $"Data Source={Path.Combine(_root, "catalog.db")};Foreign Keys=True;Pooling=False",
            ["ConnectionStrings:UsersModule"] = $"Data Source={Path.Combine(_root, "users.db")};Foreign Keys=True;Pooling=False",
            ["Startup:DevelopmentDatabaseInitializationMode"] = "Migrate",
            ["Security:RequireHttps"] = "false",
            ["JwtSettings:AccessTokenSecret"] = "integration-tests-access-token-secret-key-2026",
            ["AccessControl:IpAllowlist:Enabled"] = "false",
            ["ReverseProxy:Enabled"] = "false",
            ["FamilyAuthentication:PasswordLoginEnabled"] = "true",
            ["FamilyAuthentication:BootstrapOwner:Email"] = OwnerEmail,
            ["FamilyAuthentication:BootstrapOwner:DisplayName"] = "Integration Owner",
            ["FamilyAuthentication:BootstrapOwner:InitialPassword"] = OwnerPassword,
            ["UserSeeding:DevelopmentDefaultUser:Enabled"] = "true",
            ["UserSeeding:DevelopmentDefaultUser:Email"] = OwnerEmail,
            ["UserSeeding:DevelopmentDefaultUser:Password"] = OwnerPassword,
            ["UserSeeding:DevelopmentDefaultUser:FirstName"] = "Integration",
            ["UserSeeding:DevelopmentDefaultUser:LastName"] = "Owner",
            ["UserSeeding:DevelopmentDefaultUser:Role"] = "OWNER",
            ["MediaStorage:Backups:ScheduleEnabled"] = "false",
            ["MediaStorage:LocalDisk:StagingDirectory"] = Path.Combine(_root, "staging"),
            ["MediaStorage:LocalDisk:ThumbnailDirectory"] = Path.Combine(_root, "thumbnails"),
            ["MediaStorage:LocalDisk:OriginalCacheDirectory"] = Path.Combine(_root, "cache", "blobs", "sha256"),
            ["MediaStorage:LocalDisk:OriginalCacheMaxBytes"] = (32L * 1024 * 1024).ToString(),
            ["MediaStorage:LocalDisk:BackupDirectory"] = Path.Combine(_root, "backups")
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStoreFactory>();
            services.AddSingleton<IObjectStoreFactory>(ObjectStore);
            services.RemoveAll<IMediaMetadataExtractor>();
            services.AddSingleton<IMediaMetadataExtractor, IntegrationMetadataExtractor>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var (name, value) in _previousEnvironment) Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void SetEnvironment(string name, string value)
    {
        _previousEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class IntegrationMetadataExtractor : IMediaMetadataExtractor
    {
        public async Task<ExtractedMediaMetadata> ExtractAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = new byte[8];
            await using var stream = File.OpenRead(path);
            var read = await stream.ReadAsync(header, cancellationToken);
            var mimeType = read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff
                ? "image/jpeg"
                : read == 8 && header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })
                    ? "image/png"
                    : "application/octet-stream";
            return new ExtractedMediaMetadata(
                mimeType, null, null, null, null, null, null, null, "{}");
        }
    }
}
