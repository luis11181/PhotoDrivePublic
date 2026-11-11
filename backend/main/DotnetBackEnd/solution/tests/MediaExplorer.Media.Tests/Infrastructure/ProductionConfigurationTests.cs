using CSSApps.Api;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class ProductionConfigurationTests
{
    [Theory]
    [InlineData("deployment.json")]
    [InlineData("deployment.Development.json")]
    public void Deployment_files_contain_only_host_and_network_settings(string fileName)
    {
        using var document = ReadConfigurationDocument(fileName);
        var root = document.RootElement;

        root.EnumerateObject().Select(property => property.Name).Should().BeSubsetOf(
            ["_comments", "Urls", "AllowedHosts", "Frontend", "ReverseProxy", "AccessControl", "Security"]);
        root.GetProperty("Security").EnumerateObject().Select(property => property.Name)
            .Should().Equal("RequireHttps");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void Appsettings_do_not_duplicate_deployment_settings(string fileName)
    {
        using var document = ReadConfigurationDocument(fileName);
        var root = document.RootElement;

        root.TryGetProperty("Urls", out _).Should().BeFalse();
        root.TryGetProperty("AllowedHosts", out _).Should().BeFalse();
        root.TryGetProperty("Frontend", out _).Should().BeFalse();
        root.TryGetProperty("ReverseProxy", out _).Should().BeFalse();
        root.TryGetProperty("AccessControl", out _).Should().BeFalse();
    }

    [Fact]
    public void Tracked_runtime_files_keep_authentication_and_infrastructure_separate()
    {
        using var authentication = ReadConfigurationDocument("authentication.json");
        using var infrastructure = ReadConfigurationDocument("infrastructure.json");

        authentication.RootElement.TryGetProperty("JwtSettings", out _).Should().BeTrue();
        authentication.RootElement.TryGetProperty("OAuth", out _).Should().BeTrue();
        authentication.RootElement.TryGetProperty("ConnectionStrings", out _).Should().BeFalse();
        authentication.RootElement.TryGetProperty("Urls", out _).Should().BeFalse();

        infrastructure.RootElement.TryGetProperty("Database", out _).Should().BeTrue();
        infrastructure.RootElement.TryGetProperty("ConnectionStrings", out _).Should().BeFalse();
        infrastructure.RootElement.GetProperty("Database").TryGetProperty("AuthToken", out _).Should().BeFalse("the Turso token is supplied only through the environment");
        infrastructure.RootElement.TryGetProperty("MediaStorage", out _).Should().BeTrue();
        infrastructure.RootElement.GetProperty("Security")
            .TryGetProperty("DataProtectionKeysDirectory", out _).Should().BeTrue();
        infrastructure.RootElement.TryGetProperty("JwtSettings", out _).Should().BeFalse();
        infrastructure.RootElement.TryGetProperty("Urls", out _).Should().BeFalse();
    }

    [Fact]
    public void Development_deployment_overlay_supports_the_private_lan_and_tailscale()
    {
        var configuration = DeploymentConfiguration("Development");

        configuration["Urls"].Should().Be("http://0.0.0.0:6045");
        configuration["AllowedHosts"].Should().Contain("photodrive.local");
        configuration.GetValue<bool>("Security:RequireHttps").Should().BeFalse();
        configuration.GetValue<bool>("ReverseProxy:Enabled").Should().BeTrue();
        configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>()
            .Should().BeEquivalentTo("127.0.0.1", "::1");
        configuration.GetValue<bool>("AccessControl:IpAllowlist:Enabled").Should().BeTrue();
        configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>()
            .Should().Contain("http://photodrive.local:3000");
        configuration.GetSection("Frontend:AllowedHttpHostsAnyPort").Get<string[]>()
            .Should().Contain("photodrive");
        configuration.GetSection("AccessControl:IpAllowlist:AllowedNetworks").Get<string[]>()
            .Should().Contain(["192.168.0.0/16", "100.64.0.0/10", "fd7a:115c:a1e0::/48"]);
    }

    [Fact]
    public void Production_deployment_supports_the_private_lan_and_tailscale()
    {
        var configuration = DeploymentConfiguration("Production");

        configuration["Urls"].Should().Be("http://0.0.0.0:6065");
        configuration["AllowedHosts"].Should().Contain("photodrive.local");
        configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>()
            .Should().Contain("http://photodrive.local:60020");
        configuration.GetSection("Frontend:AllowedHttpHostsAnyPort").Get<string[]>()
            .Should().Contain("photodrive");
        configuration.GetValue<bool>("Security:RequireHttps").Should().BeFalse();
        configuration.GetValue<bool>("ReverseProxy:Enabled").Should().BeTrue();
        configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>()
            .Should().BeEquivalentTo("127.0.0.1", "::1");
        configuration.GetValue<bool>("AccessControl:IpAllowlist:Enabled").Should().BeTrue();
        configuration.GetSection("AccessControl:IpAllowlist:AllowedNetworks").Get<string[]>()
            .Should().Contain([
                "127.0.0.1/32",
                "::1/128",
                "192.168.0.0/16",
                "100.64.0.0/10",
                "fd7a:115c:a1e0::/48"]);
    }

    [Fact]
    public void Private_http_origin_is_allowed_only_when_https_enforcement_is_disabled()
    {
        var privateHttp = Configuration(new Dictionary<string, string?>
        {
            ["Security:RequireHttps"] = "false",
            ["Frontend:AllowedOrigins:0"] = "http://photodrive.local:3000"
        });
        var privateMagicDnsHttp = Configuration(new Dictionary<string, string?>
        {
            ["Security:RequireHttps"] = "false",
            ["Frontend:AllowedOrigins:0"] = "http://photodrive:60020"
        });
        var publicHttp = Configuration(new Dictionary<string, string?>
        {
            ["Security:RequireHttps"] = "false",
            ["Frontend:AllowedOrigins:0"] = "http://photos.example.com"
        });

        ProductionConfigurationValidator.FindErrors(privateHttp)
            .Should().NotContain(error => error.Contains("Frontend origin", StringComparison.Ordinal));
        ProductionConfigurationValidator.FindErrors(privateMagicDnsHttp)
            .Should().NotContain(error => error.Contains("Frontend origin", StringComparison.Ordinal));
        ProductionConfigurationValidator.FindErrors(publicHttp)
            .Should().Contain(error => error.Contains("Frontend origin", StringComparison.Ordinal));
    }

    [Fact]
    public void Configured_private_http_hostname_is_allowed_on_any_port_only_for_that_hostname()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Frontend:AllowedOrigins:0"] = "https://photodrive.example-tailnet.ts.net",
            ["Frontend:AllowedHttpHostsAnyPort:0"] = "photodrive"
        }).Build();
        var policy = new FrontendOriginPolicy(configuration);

        policy.IsAllowed("http://photodrive:3000").Should().BeTrue();
        policy.IsAllowed("http://photodrive:60020").Should().BeTrue();
        policy.IsAllowed("http://photodrive:49152").Should().BeTrue();
        policy.IsAllowed("https://photodrive.example-tailnet.ts.net").Should().BeTrue();
        policy.IsAllowed("https://photodrive:49152").Should().BeFalse();
        policy.IsAllowed("http://photodrive.local:49152").Should().BeFalse();
        policy.IsAllowed("http://example.com:49152").Should().BeFalse();
    }

    [Fact]
    public void Supported_production_configuration_has_no_validation_errors()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "photos.family.test",
            ["Frontend:AllowedOrigins:0"] = "https://photos.family.test",
            ["Database:Url"] = "turso://photodrive-family.aws-us-east-1.turso.io",
            ["Database:AuthToken"] = "test-token",
            ["Database:ReplicaPath"] = Path.Combine(Path.GetTempPath(), "app-data", "databases", "photodrive.replica.db"),
            ["Security:DataProtectionKeysDirectory"] = Path.Combine(Path.GetTempPath(), "app-data", "keys"),
            ["MediaStorage:LocalDisk:StagingDirectory"] = Path.Combine(Path.GetTempPath(), "app-data", "cache", "staging"),
            ["MediaStorage:LocalDisk:ThumbnailDirectory"] = Path.Combine(Path.GetTempPath(), "app-data", "cache", "thumbnails"),
            ["MediaStorage:LocalDisk:OriginalCacheDirectory"] = Path.Combine(Path.GetTempPath(), "app-data", "cache", "blobs", "sha256"),
            ["MediaStorage:LocalDisk:BackupDirectory"] = Path.Combine(Path.GetTempPath(), "app-data", "backups"),
            ["MediaStorage:S3:BucketName"] = "media-originals",
            ["MediaStorage:S3:ProfileName"] = "mediaexplorer",
            ["MediaStorage:S3:Region"] = "us-east-1",
            ["MediaStorage:S3Backups:BucketName"] = "media-backups",
            ["MediaStorage:S3Backups:ProfileName"] = "mediaexplorer",
            ["MediaStorage:S3Backups:Region"] = "us-east-1",
            ["FamilyAuthentication:BootstrapOwner:Email"] = "owner@family.test",
            ["JwtSettings:Issuer"] = "MediaExplorer",
            ["JwtSettings:Audience"] = "MediaExplorer",
            ["JwtSettings:AccessTokenSecret"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["JwtSettings:ValidateIssuer"] = "true",
            ["JwtSettings:ValidateAudience"] = "true",
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks"] = "198.51.100.42,2001:db8::/32",
            ["ReverseProxy:Enabled"] = "true",
            ["ReverseProxy:KnownProxies:0"] = "127.0.0.1",
            ["ReverseProxy:KnownProxies:1"] = "::1"
        });

        ProductionConfigurationValidator.FindErrors(configuration).Should().BeEmpty();
    }

    [Fact]
    public void Production_rejects_the_development_turso_database()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Database:Url"] = "turso://photodrive-development-family.aws-us-east-1.turso.io",
            ["Database:AuthToken"] = "test-token"
        });

        ProductionConfigurationValidator.FindErrors(configuration)
            .Should().Contain(error => error.Contains("Development Turso database", StringComparison.Ordinal));
    }

    [Fact]
    public void Unsafe_defaults_are_reported_together()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "*",
            ["Frontend:AllowedOrigins:0"] = "http://localhost:3000",
            ["ConnectionStrings:UsersModule"] = "Data Source=users.db",
            ["ConnectionStrings:MediaCatalog"] = "Data Source=/absolute/path/catalog.db",
            ["FamilyAuthentication:BootstrapOwner:Email"] = "not-an-email",
            ["FamilyAuthentication:BootstrapOwner:InitialPassword"] = "replace-with-a-password",
            ["JwtSettings:AccessTokenSecret"] = "replace-with-at-least-32-random-bytes",
            ["JwtSettings:ValidateIssuer"] = "false",
            ["JwtSettings:ValidateAudience"] = "false",
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks"] = "0.0.0.0/0",
            ["ReverseProxy:Enabled"] = "true",
            ["ReverseProxy:KnownNetworks:0"] = "0.0.0.0/0"
        });

        var errors = ProductionConfigurationValidator.FindErrors(configuration);

        errors.Should().Contain(error => error.Contains("AllowedHosts", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("HTTPS origin", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("Database:Url", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("Database:AuthToken", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("Database:ReplicaPath", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("DataProtectionKeysDirectory", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("non-placeholder owner email", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("template placeholder", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("32 non-placeholder", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("issuer and audience", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("entire internet", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("trusts the entire internet", StringComparison.Ordinal));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static JsonDocument ReadConfigurationDocument(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, fileName)));

    private static IConfiguration DeploymentConfiguration(string environmentName) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("deployment.json", optional: false)
            .AddJsonFile($"deployment.{environmentName}.json", optional: true)
            .Build();
}
