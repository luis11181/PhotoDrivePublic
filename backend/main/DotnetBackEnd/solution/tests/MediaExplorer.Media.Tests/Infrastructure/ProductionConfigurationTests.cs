using CSSApps.Api;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class ProductionConfigurationTests
{
    [Fact]
    public void Tracked_appsettings_uses_portable_paths_and_placeholders()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        configuration.GetConnectionString("UsersModule").Should().Contain("/data/databases/");
        configuration.GetConnectionString("MediaCatalog").Should().Contain("/data/databases/");
        configuration["Security:DataProtectionKeysDirectory"].Should().Be("/data/keys");
        configuration["MediaStorage:LocalDisk:StagingDirectory"].Should().StartWith("/data/");
        configuration["MediaStorage:S3:BucketName"].Should().StartWith("replace-");
        configuration["MediaStorage:S3Backups:BucketName"].Should().StartWith("replace-");
        configuration["JwtSettings:AccessTokenSecret"].Should().StartWith("replace-");
        configuration["FamilyAuthentication:BootstrapOwner:Email"].Should().Be("owner@example.com");
        configuration.GetValue<bool>("AccessControl:IpAllowlist:Enabled").Should().BeFalse();
    }

    [Fact]
    public void Private_http_origin_is_allowed_only_when_https_enforcement_is_disabled()
    {
        var privateHttp = Configuration(new Dictionary<string, string?>
        {
            ["Security:RequireHttps"] = "false",
            ["Frontend:AllowedOrigins:0"] = "http://family-drive.local:8080"
        });
        var publicHttp = Configuration(new Dictionary<string, string?>
        {
            ["Security:RequireHttps"] = "false",
            ["Frontend:AllowedOrigins:0"] = "http://photos.example.net"
        });

        ProductionConfigurationValidator.FindErrors(privateHttp)
            .Should().NotContain(error => error.Contains("Frontend origin", StringComparison.Ordinal));
        ProductionConfigurationValidator.FindErrors(publicHttp)
            .Should().Contain(error => error.Contains("Frontend origin", StringComparison.Ordinal));
    }

    [Fact]
    public void Configured_private_http_hostname_is_allowed_on_any_port_only_for_that_hostname()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Frontend:AllowedOrigins:0"] = "https://photos.family.test",
            ["Frontend:AllowedHttpHostsAnyPort:0"] = "family-drive"
        }).Build();
        var policy = new FrontendOriginPolicy(configuration);

        policy.IsAllowed("http://family-drive:3000").Should().BeTrue();
        policy.IsAllowed("http://family-drive:8080").Should().BeTrue();
        policy.IsAllowed("https://photos.family.test").Should().BeTrue();
        policy.IsAllowed("https://family-drive:8080").Should().BeFalse();
        policy.IsAllowed("http://family-drive.local:8080").Should().BeFalse();
        policy.IsAllowed("http://example.com:8080").Should().BeFalse();
    }

    [Fact]
    public void Supported_production_configuration_has_no_validation_errors()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "photos.family.test",
            ["Frontend:AllowedOrigins:0"] = "https://photos.family.test",
            ["ConnectionStrings:UsersModule"] = "Data Source=/data/databases/users.db;Foreign Keys=True",
            ["ConnectionStrings:MediaCatalog"] = "Data Source=/data/databases/catalog.db;Foreign Keys=True",
            ["Security:DataProtectionKeysDirectory"] = "/data/keys",
            ["MediaStorage:LocalDisk:StagingDirectory"] = "/data/cache/staging",
            ["MediaStorage:LocalDisk:ThumbnailDirectory"] = "/data/cache/thumbnails",
            ["MediaStorage:LocalDisk:OriginalCacheDirectory"] = "/data/cache/originals",
            ["MediaStorage:LocalDisk:BackupDirectory"] = "/data/backups",
            ["MediaStorage:S3:BucketName"] = "family-originals",
            ["MediaStorage:S3:ProfileName"] = "photodrive",
            ["MediaStorage:S3:Region"] = "us-east-1",
            ["MediaStorage:S3Backups:BucketName"] = "family-backups",
            ["MediaStorage:S3Backups:ProfileName"] = "photodrive",
            ["MediaStorage:S3Backups:Region"] = "us-east-1",
            ["FamilyAuthentication:BootstrapOwner:Email"] = "owner@family.test",
            ["JwtSettings:Issuer"] = "https://photos.family.test",
            ["JwtSettings:Audience"] = "https://photos.family.test",
            ["JwtSettings:AccessTokenSecret"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            ["JwtSettings:ValidateIssuer"] = "true",
            ["JwtSettings:ValidateAudience"] = "true",
            ["AccessControl:IpAllowlist:Enabled"] = "false",
            ["ReverseProxy:Enabled"] = "false"
        });

        ProductionConfigurationValidator.FindErrors(configuration).Should().BeEmpty();
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
        errors.Should().Contain(error => error.Contains("UsersModule", StringComparison.Ordinal));
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
}
