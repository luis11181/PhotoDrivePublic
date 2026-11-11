using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MediaExplorer.Media.Tests.Api;

[Collection("Web application")]
public sealed class MediaApiIntegrationTests
{
    [Fact]
    public async Task Refresh_rotates_http_only_cookies_and_replay_terminates_the_session()
    {
        using var factory = new MediaApiWebApplicationFactory();
        using var browser = factory.CreateClient(ClientOptions());

        var login = await browser.PostAsJsonAsync("/api/v1/users/login", new
        {
            email = MediaApiWebApplicationFactory.OwnerEmail,
            password = MediaApiWebApplicationFactory.OwnerPassword
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        var originalRefreshCookie = SetCookie(login, "refresh_token");
        var originalRefreshToken = CookieValue(originalRefreshCookie);
        originalRefreshCookie.Should().ContainEquivalentOf("httponly");
        originalRefreshCookie.Should().ContainEquivalentOf("samesite=strict");
        originalRefreshCookie.Should().ContainEquivalentOf("path=/api/v1/users/refresh-token");
        SetCookie(login, "access_token").Should().ContainEquivalentOf("path=/");

        var currentUser = await browser.GetAsync(Relative("/api/v1/users/me"));
        currentUser.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrf = currentUser.Headers.GetValues("X-CSRF-Token").Single();

        var missingCsrf = await browser.PostAsync(Relative("/api/v1/users/refresh-token"), content: null);
        missingCsrf.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users/refresh-token");
        refreshRequest.Headers.Add("X-CSRF-Token", csrf);
        var refresh = await browser.SendAsync(refreshRequest);
        refresh.StatusCode.Should().Be(HttpStatusCode.OK, await refresh.Content.ReadAsStringAsync());
        var rotatedRefreshCookie = SetCookie(refresh, "refresh_token");
        CookieValue(rotatedRefreshCookie).Should().NotBe(originalRefreshToken);
        SetCookie(refresh, "access_token").Should().ContainEquivalentOf("httponly");
        var refreshBody = await refresh.Content.ReadAsStringAsync();
        refreshBody.Should().NotContainEquivalentOf("jwtToken");
        refreshBody.Should().NotContainEquivalentOf("refreshToken");
        (await browser.GetAsync(Relative("/api/v1/users/me"))).StatusCode.Should().Be(HttpStatusCode.OK);

        using var replay = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = new Uri("http://localhost")
        });
        var replayCsrf = await replay.GetAsync(Relative("/api/v1/users/csrf-token"));
        replayCsrf.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var replayCsrfToken = replayCsrf.Headers.GetValues("X-CSRF-Token").Single();
        var replayCsrfCookie = SetCookie(replayCsrf, "media_explorer_csrf").Split(';', 2)[0];
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/users/refresh-token");
        replayRequest.Headers.Add("X-CSRF-Token", replayCsrfToken);
        replayRequest.Headers.Add("Cookie", $"{replayCsrfCookie}; refresh_token={originalRefreshToken}");
        var replayResponse = await replay.SendAsync(replayRequest);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await browser.GetAsync(Relative("/api/v1/users/me"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Owner_and_member_journey_enforces_session_csrf_sharing_and_restore()
    {
        using var factory = new MediaApiWebApplicationFactory();
        using var anonymous = factory.CreateClient(ClientOptions());

        (await anonymous.GetAsync(Relative("/api/v1/items"))).StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        using var owner = factory.CreateClient(ClientOptions());
        var ownerCsrf = await LoginAsync(owner, MediaApiWebApplicationFactory.OwnerEmail, MediaApiWebApplicationFactory.OwnerPassword);
        var me = await owner.GetAsync(Relative("/api/v1/users/me"));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        ownerCsrf = me.Headers.GetValues("X-CSRF-Token").Single();

        var withoutCsrf = await owner.PostAsJsonAsync("/api/v1/items/folders", new { name = "Rejected folder" });
        withoutCsrf.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var folder = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/items/folders", new { name = "Integration folder" }, ownerCsrf);
        folder.StatusCode.Should().Be(HttpStatusCode.Created);
        using var folderDocument = await JsonDocument.ParseAsync(await folder.Content.ReadAsStreamAsync());
        var folderId = folderDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var member = await InviteMemberAsync(owner, ownerCsrf);
        using var memberClient = factory.CreateClient(ClientOptions());
        var memberCsrf = await LoginAsync(memberClient, member.Email, member.Password);
        (await memberClient.GetAsync(Relative("/api/v1/developer/maintenance/backup-status"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        foreach (var route in new[] { "/api/v1/imports", "/api/v1/imports/options", "/api/v1/imports/source-folders" })
            (await memberClient.GetAsync(Relative(route))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendJsonAsync(memberClient, HttpMethod.Post, "/api/v1/imports/google-takeout", new { sourcePath = "", destinationFolderId = folderId }, memberCsrf)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendJsonAsync(memberClient, HttpMethod.Post, $"/api/v1/imports/{Guid.NewGuid()}/retry", new { }, memberCsrf)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var sources = await owner.GetAsync(Relative("/api/v1/imports/source-folders"));
        sources.StatusCode.Should().Be(HttpStatusCode.OK);
        var sourceJson = await sources.Content.ReadAsStringAsync();
        sourceJson.Should().Contain("Takeout").And.NotContain("media-explorer-api-tests-");
        (await owner.GetAsync(Relative("/api/v1/imports/source-folders?path=..%2Fprivate"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var bytes = "0123456789abcdef"u8.ToArray();
        var itemId = await UploadAsync(owner, ownerCsrf, bytes, folderId);

        var folderPreview = await owner.GetAsync(Relative($"/api/v1/items/folders/{folderId}/download/preview"));
        folderPreview.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var folderPreviewDocument = await JsonDocument.ParseAsync(await folderPreview.Content.ReadAsStreamAsync()))
        {
            var previewData = folderPreviewDocument.RootElement.GetProperty("data");
            previewData.GetProperty("fileCount").GetInt32().Should().Be(1);
            previewData.GetProperty("totalBytes").GetInt64().Should().Be(bytes.Length);
        }

        var folderArchive = await owner.GetAsync(Relative($"/api/v1/items/folders/{folderId}/download"));
        folderArchive.StatusCode.Should().Be(HttpStatusCode.OK);
        folderArchive.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        using (var folderArchiveData = new MemoryStream(await folderArchive.Content.ReadAsByteArrayAsync()))
        using (var zip = new ZipArchive(folderArchiveData, ZipArchiveMode.Read))
        {
            zip.Entries.Should().ContainSingle(entry => !entry.FullName.EndsWith('/'));
            await using var archivedFile = await zip.Entries.Single(entry => !entry.FullName.EndsWith('/')).OpenAsync(default);
            await using var archivedBytes = new MemoryStream();
            await archivedFile.CopyToAsync(archivedBytes);
            archivedBytes.ToArray().Should().Equal(bytes);
        }

        var createAlbum = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/albums", new { name = "Integration album" }, ownerCsrf);
        createAlbum.StatusCode.Should().Be(HttpStatusCode.OK);
        using var albumDocument = await JsonDocument.ParseAsync(await createAlbum.Content.ReadAsStreamAsync());
        var albumId = albumDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var albumItemId = await UploadAsync(owner, ownerCsrf, MetadataFixtures.ValidExifJpeg());
        var mixedArchive = await owner.GetAsync(Relative(
            $"/api/v1/items/download?ids={folderId}&ids={albumItemId}"));
        mixedArchive.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var mixedArchiveData = new MemoryStream(await mixedArchive.Content.ReadAsByteArrayAsync()))
        using (var zip = new ZipArchive(mixedArchiveData, ZipArchiveMode.Read))
        {
            var exportedFiles = zip.Entries.Where(entry => !entry.FullName.EndsWith('/')).ToList();
            exportedFiles.Should().HaveCount(2);
            exportedFiles.Should().ContainSingle(entry => entry.FullName.Contains('/'));
            exportedFiles.Should().ContainSingle(entry => !entry.FullName.Contains('/'));
        }
        await using (var albumScope = factory.Services.CreateAsyncScope())
        {
            var catalog = albumScope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
            var albumLibraryId = await catalog.Albums.Where(album => album.Id == albumId).Select(album => album.LibraryId).SingleAsync();
            var albumFile = await catalog.Files.AsNoTracking().SingleAsync(file => file.Id == albumItemId);
            albumFile.LibraryId.Should().Be(albumLibraryId);
            albumFile.TrashedAt.Should().BeNull();
            albumFile.MimeType.Should().Match(type => type.StartsWith("image/") || type.StartsWith("video/"));
        }
        var addAlbumItem = await SendJsonAsync(owner, HttpMethod.Put, $"/api/v1/albums/{albumId}/items/{albumItemId}", body: null, ownerCsrf);
        addAlbumItem.StatusCode.Should().Be(HttpStatusCode.OK, await addAlbumItem.Content.ReadAsStringAsync());
        var albumPreview = await owner.GetAsync(Relative($"/api/v1/albums/{albumId}/download/preview"));
        albumPreview.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var albumPreviewDocument = await JsonDocument.ParseAsync(await albumPreview.Content.ReadAsStreamAsync()))
        {
            albumPreviewDocument.RootElement.GetProperty("data").GetProperty("fileCount").GetInt32().Should().Be(1);
        }
        var albumArchive = await owner.GetAsync(Relative($"/api/v1/albums/{albumId}/download"));
        albumArchive.StatusCode.Should().Be(HttpStatusCode.OK);
        albumArchive.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        using (var albumArchiveData = new MemoryStream(await albumArchive.Content.ReadAsByteArrayAsync()))
        using (var zip = new ZipArchive(albumArchiveData, ZipArchiveMode.Read))
        {
            zip.Entries.Should().ContainSingle(entry => !entry.FullName.EndsWith('/'));
        }

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/items/{itemId}/content");
        rangeRequest.Headers.Range = new RangeHeaderValue(4, 8);
        var range = await owner.SendAsync(rangeRequest);
        range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await range.Content.ReadAsByteArrayAsync()).Should().Equal(bytes[4..9]);
        range.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 4-8/{bytes.Length}");

        var share = await SendJsonAsync(owner, HttpMethod.Put, $"/api/v1/shares/items/{itemId}/{member.Id}", new { role = "viewer" }, ownerCsrf);
        share.StatusCode.Should().Be(HttpStatusCode.OK);
        (await memberClient.GetAsync(Relative($"/api/v1/items/{itemId}"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var memberRename = await SendJsonAsync(memberClient, HttpMethod.Patch, $"/api/v1/items/{itemId}", new { name = "Not allowed.bin" }, memberCsrf);
        memberRename.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.Forbidden);

        var revoke = await SendJsonAsync(owner, HttpMethod.Delete, $"/api/v1/shares/items/{itemId}/{member.Id}", body: null, ownerCsrf);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        (await memberClient.GetAsync(Relative($"/api/v1/items/{itemId}"))).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await SendJsonAsync(owner, HttpMethod.Post, $"/api/v1/items/{itemId}/trash", body: null, ownerCsrf)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendJsonAsync(owner, HttpMethod.Post, $"/api/v1/items/{itemId}/restore", body: null, ownerCsrf)).StatusCode.Should().Be(HttpStatusCode.OK);
        var restored = await owner.GetAsync(Relative($"/api/v1/items/{itemId}/content"));
        restored.StatusCode.Should().Be(HttpStatusCode.OK);
        (await restored.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);

        await SeedOperationalAlertsAsync(factory);
        var health = await owner.GetAsync(Relative("/api/v1/developer/maintenance/health"));
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        using var healthDocument = await JsonDocument.ParseAsync(await health.Content.ReadAsStreamAsync());
        var healthData = healthDocument.RootElement.GetProperty("data");
        healthData.GetProperty("state").GetString().Should().Be("attention");
        healthData.GetProperty("staleUploadCount").GetInt32().Should().BeGreaterThan(0);
        healthData.GetProperty("failedMetadataCount").GetInt32().Should().BeGreaterThan(0);
        healthData.GetProperty("repeatedWorkerFailureCount").GetInt32().Should().BeGreaterThan(0);
    }

    private static WebApplicationFactoryClientOptions ClientOptions() => new()
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
        BaseAddress = new Uri("http://localhost")
    };

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/v1/users/login", new { email, password });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.TryGetValues("X-CSRF-Token", out var values).Should().BeTrue();
        return values!.Single();
    }

    private static string SetCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith($"{name}=", StringComparison.Ordinal));

    private static string CookieValue(string setCookie) =>
        setCookie.Split(';', 2)[0].Split('=', 2)[1];

    private static async Task<(Guid Id, string Email, string Password)> InviteMemberAsync(HttpClient owner, string csrf)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"member-{suffix}@example.test";
        const string password = "IntegrationMember!2026";
        var invite = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/users/admin", new
        {
            email,
            displayName = "Integration Member",
            role = "MEMBER",
            googleAllowed = false,
            passwordAllowed = true
        }, csrf);
        invite.StatusCode.Should().Be(HttpStatusCode.OK, await invite.Content.ReadAsStringAsync());
        using var document = await JsonDocument.ParseAsync(await invite.Content.ReadAsStreamAsync());
        var data = document.RootElement.GetProperty("data");
        var id = data.GetProperty("user").GetProperty("id").GetGuid();
        var code = data.GetProperty("passwordSetup").GetProperty("code").GetString()!;

        // The reset endpoint is anonymous; use the factory-backed handler through the same client without a CSRF header.
        var reset = await owner.PostAsJsonAsync("/api/v1/users/reset-password", new { code, password });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        return (id, email, password);
    }

    private static async Task<Guid> UploadAsync(HttpClient owner, string csrf, byte[] bytes, Guid? parentId = null)
    {
        var create = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/uploads", new
        {
            clientUploadId = Guid.NewGuid(),
            name = $"integration-{Guid.NewGuid():N}.jpg",
            mimeType = "image/jpeg",
            sizeBytes = bytes.Length,
            parentId,
            useDefaultMediaFolder = !parentId.HasValue,
            useDefaultDocumentsFolder = false,
            allowNameConflict = false
        }, csrf);
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        using var createDocument = await JsonDocument.ParseAsync(await create.Content.ReadAsStreamAsync());
        var uploadId = createDocument.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var chunk = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/uploads/{uploadId}");
        chunk.Headers.Add("X-CSRF-Token", csrf);
        chunk.Content = new ByteArrayContent(bytes);
        chunk.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        chunk.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length);
        var appended = await owner.SendAsync(chunk);
        appended.StatusCode.Should().Be(HttpStatusCode.OK, await appended.Content.ReadAsStringAsync());

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var complete = await SendJsonAsync(owner, HttpMethod.Post, $"/api/v1/uploads/{uploadId}/complete", new { sha256, allowNameConflict = false }, csrf);
        complete.StatusCode.Should().Be(HttpStatusCode.OK, await complete.Content.ReadAsStringAsync());
        using var completeDocument = await JsonDocument.ParseAsync(await complete.Content.ReadAsStreamAsync());
        return completeDocument.RootElement.GetProperty("data").GetProperty("item").GetProperty("id").GetGuid();
    }

    private static async Task SeedOperationalAlertsAsync(MediaApiWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MediaCatalogDbContext>();
        var library = await dbContext.Libraries.AsNoTracking().FirstAsync();
        dbContext.UploadSessions.Add(new UploadSession
        {
            UserId = library.OwnerUserId,
            LibraryId = library.Id,
            Name = "private-test-value.bin",
            MimeType = "application/octet-stream",
            SizeBytes = 1,
            ReceivedBytes = 0,
            StagingFileName = "opaque-test-staging",
            State = "pending",
            CreatedAt = DateTime.UtcNow.AddHours(-25)
        });
        var metadata = await dbContext.FileVersionMetadata.FirstAsync();
        metadata.State = "failed";
        metadata.AttemptCount = 3;
        metadata.ProcessingStartedAt = null;
        await dbContext.SaveChangesAsync();
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

[CollectionDefinition("Web application", DisableParallelization = true)]
public sealed class WebApplicationTestGroup;
