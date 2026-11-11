using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MediaExplorer.Media.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using TursoConnection = Turso.Data.Sqlite.SqliteConnection;

namespace MediaExplorer.Media.Tests.Api;

[Collection("Web application")]
public sealed class DatabaseBackupAndHostsApiTests
{
    [Fact]
    public async Task Backup_produces_a_verified_plain_sqlite_snapshot_and_records_status_in_the_shared_database()
    {
        using var factory = new MediaApiWebApplicationFactory();
        using var owner = factory.CreateClient(ClientOptions());
        var csrf = await LoginAsync(owner);
        (await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/items/folders", new { name = "Cumpleaños" }, csrf)).StatusCode.Should().Be(HttpStatusCode.Created);

        var backup = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/developer/maintenance/backup", null, csrf);
        backup.StatusCode.Should().Be(HttpStatusCode.OK, await backup.Content.ReadAsStringAsync());
        using (var document = await JsonDocument.ParseAsync(await backup.Content.ReadAsStreamAsync()))
        {
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("remoteBackupVerified").GetBoolean().Should().BeTrue();
            data.GetProperty("userCount").GetInt64().Should().BeGreaterThan(0);
            data.GetProperty("folderCount").GetInt64().Should().BeGreaterThanOrEqualTo(3);
        }

        var snapshot = factory.BackupStore.Uploaded.Should().ContainSingle().Subject;
        var header = new byte[20];
        await using (var stream = File.OpenRead(snapshot.SnapshotPath)) await stream.ReadExactlyAsync(header);
        Encoding.ASCII.GetString(header, 0, 15).Should().Be("SQLite format 3");
        header[18].Should().Be(2, "the backup must be a WAL-mode SQLite file that `turso db import` accepts");
        header[19].Should().Be(2);
        await using (var connection = new TursoConnection($"Data Source={snapshot.SnapshotPath};Mode=ReadOnly"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name LIKE 'turso_%';";
            Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(0);
            command.CommandText = "SELECT COUNT(*) FROM folders WHERE Name = 'Cumpleaños';";
            Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(1);
        }
        (await File.ReadAllTextAsync(snapshot.ManifestPath)).Should().Contain(snapshot.SnapshotSha256);

        using (var status = await JsonDocument.ParseAsync(await (await owner.GetAsync(Relative("/api/v1/developer/maintenance/backup-status"))).Content.ReadAsStreamAsync()))
        {
            status.RootElement.GetProperty("data").GetProperty("state").GetString().Should().Be("current");
            status.RootElement.GetProperty("data").GetProperty("freshnessWindowHours").GetInt32().Should().Be(7 * 24);
        }

        factory.BackupStore.FailNext = true;
        var failed = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/developer/maintenance/backup", null, csrf);
        failed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await failed.Content.ReadAsStringAsync()).Should().Contain("remote_database_backup_failed");
        using (var health = await JsonDocument.ParseAsync(await (await owner.GetAsync(Relative("/api/v1/developer/maintenance/health"))).Content.ReadAsStreamAsync()))
        {
            var data = health.RootElement.GetProperty("data");
            data.GetProperty("consecutiveBackupFailureCount").GetInt32().Should().Be(1);
            data.GetProperty("isPrimaryHost").GetBoolean().Should().BeTrue();
            data.GetProperty("issues").EnumerateArray().Select(issue => issue.GetProperty("code").GetString()).Should().Contain("backup_failed");
        }
    }

    [Fact]
    public async Task First_host_becomes_primary_and_only_known_hosts_can_take_over()
    {
        using var factory = new MediaApiWebApplicationFactory();
        using var owner = factory.CreateClient(ClientOptions());
        var csrf = await LoginAsync(owner);

        var hosts = await owner.GetAsync(Relative("/api/v1/developer/maintenance/hosts"));
        hosts.StatusCode.Should().Be(HttpStatusCode.OK);
        Guid currentId;
        using (var document = await JsonDocument.ParseAsync(await hosts.Content.ReadAsStreamAsync()))
        {
            var data = document.RootElement.GetProperty("data");
            currentId = data.GetProperty("currentHostId").GetGuid();
            data.GetProperty("primaryHostId").GetGuid().Should().Be(currentId);
            var host = data.GetProperty("hosts").EnumerateArray().Should().ContainSingle().Subject;
            host.GetProperty("isCurrent").GetBoolean().Should().BeTrue();
            host.GetProperty("isPrimary").GetBoolean().Should().BeTrue();
            host.GetProperty("online").GetBoolean().Should().BeTrue();
        }

        (await SendJsonAsync(owner, HttpMethod.Put, "/api/v1/developer/maintenance/primary-host", new { hostId = Guid.NewGuid() }, csrf))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendJsonAsync(owner, HttpMethod.Put, "/api/v1/developer/maintenance/primary-host", new { hostId = currentId }, csrf))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("http://localhost")
    };

    private static async Task<string> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/users/login", new { email = MediaApiWebApplicationFactory.OwnerEmail, password = MediaApiWebApplicationFactory.OwnerPassword });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var me = await client.GetAsync(Relative("/api/v1/users/me"));
        return me.Headers.GetValues("X-CSRF-Token").Single();
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient client, HttpMethod method, string path, object? body, string csrf)
    {
        using var request = new HttpRequestMessage(method, Relative(path));
        request.Headers.Add("X-CSRF-Token", csrf);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static Uri Relative(string path) => new(path, UriKind.Relative);
}

internal sealed class FakeDatabaseBackupRemoteStore : IDatabaseBackupRemoteStore
{
    public List<DatabaseBackupVerification> Uploaded { get; } = [];
    public bool FailNext { get; set; }

    public Task<RemoteDatabaseBackupVerification> UploadAndVerifyAsync(DatabaseBackupVerification backup, CancellationToken cancellationToken)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new IOException("Simulated remote backup failure.");
        }
        Uploaded.Add(backup);
        return Task.FromResult(new RemoteDatabaseBackupVerification(backup.BackupSetId, 2, backup.SnapshotBytes));
    }
}
