using CSSApps.Api;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class SanitizedConfigurationReportTests
{
    [Fact]
    public void Report_contains_operational_state_without_sensitive_values_or_database_filenames()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), "media", "databases");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:6045",
            ["AllowedHosts"] = "api.family.test;api2.family.test",
            ["Frontend:AllowedOrigins:0"] = "https://photos.family.test",
            ["Security:RequireHttps"] = "false",
            ["ReverseProxy:Enabled"] = "true",
            ["ReverseProxy:KnownProxies:0"] = "127.0.0.1",
            ["AccessControl:IpAllowlist:Enabled"] = "true",
            ["AccessControl:IpAllowlist:AllowedNetworks:0"] = "198.51.100.42/32",
            ["Database:Url"] = "turso://private-family-db.aws-us-east-1.turso.io",
            ["Database:AuthToken"] = "private-turso-token",
            ["Database:ReplicaPath"] = Path.Combine(databaseDirectory, "private-replica.db"),
            ["Security:DataProtectionKeysDirectory"] = "/srv/media/keys",
            ["MediaStorage:LocalDisk:StagingDirectory"] = "/srv/media/cache/staging",
            ["MediaStorage:LocalDisk:ThumbnailDirectory"] = "/srv/media/cache/thumbnails",
            ["MediaStorage:LocalDisk:OriginalCacheDirectory"] = "/srv/media/cache/originals",
            ["MediaStorage:LocalDisk:BackupDirectory"] = "/srv/media/backups",
            ["MediaStorage:Backups:ScheduleEnabled"] = "true",
            ["MediaStorage:Backups:IntervalDays"] = "7",
            ["MediaStorage:Backups:RetainedSets"] = "8",
            ["JwtSettings:AccessTokenSecret"] = "super-secret-access-token-value",
            ["FamilyAuthentication:BootstrapOwner:Email"] = "private-owner@family.test",
            ["MediaStorage:S3:BucketName"] = "private-originals-bucket",
            ["MediaStorage:S3:ProfileName"] = "private-profile"
        }).Build();

        var report = SanitizedConfigurationReport.Create(
            configuration,
            "Production",
            AppContext.BaseDirectory).ToDisplayText();

        report.Should().Contain("api.family.test, api2.family.test");
        report.Should().Contain("https://photos.family.test");
        report.Should().Contain("HTTPS enforcement: disabled");
        report.Should().Contain("trusted addresses=1");
        report.Should().Contain("entries=1");
        report.Should().Contain(databaseDirectory);
        report.Should().Contain("Turso Cloud with local replica");
        report.Should().Contain("intervalDays=7; retainedSets=8");
        report.Should().NotContain("198.51.100.42");
        report.Should().NotContain("private-replica.db");
        report.Should().NotContain("private-family-db");
        report.Should().NotContain("private-turso-token");
        report.Should().NotContain("super-secret-access-token-value");
        report.Should().NotContain("private-owner@family.test");
        report.Should().NotContain("private-originals-bucket");
        report.Should().NotContain("private-profile");
    }
}
