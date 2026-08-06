using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class AlbumTests
{
    [Fact]
    public async Task Trash_preview_reports_Photos_and_distinct_album_impact_without_changing_membership()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var file = await UploadPhotoAsync(catalog, dbContext, ownerId, "memory.jpg");
        var albums = new AlbumService(dbContext, new LibraryBootstrapper(dbContext), new TestDirectory(), NullLogger<AlbumService>.Instance);
        var first = (await albums.CreateAsync(ownerId, new CreateAlbumReq { Name = "First" }, default)).SuccessValue;
        var second = (await albums.CreateAsync(ownerId, new CreateAlbumReq { Name = "Second" }, default)).SuccessValue;
        await albums.AddItemAsync(ownerId, first.Id, file.Id, default);
        await albums.AddItemAsync(ownerId, second.Id, file.Id, default);

        var preview = (await catalog.PreviewTrashAsync(ownerId, [file.Id], default)).SuccessValue;

        preview.SelectedRootCount.Should().Be(1);
        preview.FileCount.Should().Be(1);
        preview.FolderCount.Should().Be(0);
        preview.PhotoItemCount.Should().Be(1);
        preview.AlbumCount.Should().Be(2);
        (await catalog.BrowsePhotosAsync(ownerId, "folder", null, null, 100, "all", false, "captured_desc", null, null, default))
            .SuccessValue.Items.Should().ContainSingle(item => item.Id == file.Id)
            .Which.CatalogPath.Should().Be("Media");
        (await dbContext.AlbumItems.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Shared_album_authorizes_only_current_members_and_preserves_owner_mutations()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var file = await UploadPhotoAsync(catalog, dbContext, ownerId, "summer.jpg");
        var directory = new TestDirectory(new ShareRecipientProfile(recipientId, "Recipient", "recipient@example.test"));
        var albums = new AlbumService(dbContext, new LibraryBootstrapper(dbContext), directory, NullLogger<AlbumService>.Instance);

        var album = (await albums.CreateAsync(ownerId, new CreateAlbumReq { Name = "Summer", Description = "Family trip" }, default)).SuccessValue;
        (await albums.AddItemAsync(ownerId, album.Id, file.Id, default)).IsSuccess.Should().BeTrue();
        (await albums.SetShareAsync(ownerId, album.Id, recipientId, new SetItemShareReq(), default)).IsSuccess.Should().BeTrue();

        var shared = (await albums.GetAsync(recipientId, album.Id, new BrowseAlbumItemsReq(), default)).SuccessValue;
        shared.Album.AccessRole.Should().Be("viewer");
        shared.Items.Should().ContainSingle(item => item.Id == file.Id && item.AccessRole == "viewer");
        (await catalog.OpenContentAsync(recipientId, file.Id, null, null, default)).IsSuccess.Should().BeTrue();
        (await albums.UpdateAsync(recipientId, album.Id, new UpdateAlbumReq { Name = "Nope" }, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);

        (await albums.RemoveItemAsync(ownerId, album.Id, file.Id, default)).IsSuccess.Should().BeTrue();
        (await catalog.GetItemAsync(recipientId, file.Id, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
        (await albums.GetAsync(recipientId, album.Id, new BrowseAlbumItemsReq(), default)).SuccessValue.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Album_membership_is_idempotent_and_deleting_album_never_deletes_files()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var file = await UploadPhotoAsync(catalog, dbContext, ownerId, "portrait.jpg");
        var albums = new AlbumService(dbContext, new LibraryBootstrapper(dbContext), new TestDirectory(), NullLogger<AlbumService>.Instance);
        var album = (await albums.CreateAsync(ownerId, new CreateAlbumReq { Name = "People" }, default)).SuccessValue;

        (await albums.AddItemAsync(ownerId, album.Id, file.Id, default)).IsSuccess.Should().BeTrue();
        (await albums.AddItemAsync(ownerId, album.Id, file.Id, default)).IsSuccess.Should().BeTrue();
        (await dbContext.AlbumItems.CountAsync()).Should().Be(1);
        (await albums.DeleteAsync(ownerId, album.Id, default)).IsSuccess.Should().BeTrue();

        (await dbContext.Albums.CountAsync()).Should().Be(0);
        (await dbContext.AlbumItems.CountAsync()).Should().Be(0);
        (await catalog.GetItemAsync(ownerId, file.Id, default)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Album_membership_batch_is_transactional_reports_outcomes_and_derives_up_to_four_cover_tiles()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var files = new[]
        {
            await UploadPhotoAsync(catalog, dbContext, ownerId, "one.jpg"),
            await UploadPhotoAsync(catalog, dbContext, ownerId, "two.jpg"),
            await UploadPhotoAsync(catalog, dbContext, ownerId, "three.jpg"),
            await UploadPhotoAsync(catalog, dbContext, ownerId, "four.jpg")
        };
        var albums = new AlbumService(
            dbContext,
            new LibraryBootstrapper(dbContext),
            new TestDirectory(),
            NullLogger<AlbumService>.Instance);
        var album = (await albums.CreateAsync(
            ownerId,
            new CreateAlbumReq { Name = "Batch" },
            default)).SuccessValue;
        var missingId = Guid.NewGuid();

        var added = (await albums.SetItemsAsync(
            ownerId,
            album.Id,
            new SetAlbumItemsReq
            {
                ItemIds = [.. files.Select(file => file.Id), missingId],
                Included = true
            },
            default)).SuccessValue;

        added.RequestedCount.Should().Be(5);
        added.SucceededCount.Should().Be(4);
        added.FailedCount.Should().Be(1);
        added.Results.Single(result => result.ItemId == missingId).Status.Should().Be("failed");
        var listed = (await albums.ListAsync(ownerId, "owned", default)).SuccessValue.Albums.Single();
        listed.MemberCount.Should().Be(4);
        listed.CoverFileIds.Should().HaveCount(4);
        listed.CoverFileIds.Should().BeEquivalentTo(files.Select(file => file.Id));
        listed.CoverFileId.Should().Be(listed.CoverFileIds[0]);

        var repeated = (await albums.SetItemsAsync(
            ownerId,
            album.Id,
            new SetAlbumItemsReq
            {
                ItemIds = [files[0].Id, files[1].Id],
                Included = true
            },
            default)).SuccessValue;
        repeated.UnchangedCount.Should().Be(2);

        var removed = (await albums.SetItemsAsync(
            ownerId,
            album.Id,
            new SetAlbumItemsReq
            {
                ItemIds = [files[0].Id, files[1].Id, files[2].Id],
                Included = false
            },
            default)).SuccessValue;
        removed.SucceededCount.Should().Be(3);
        var afterRemove = (await albums.ListAsync(ownerId, "owned", default)).SuccessValue.Albums.Single();
        afterRemove.MemberCount.Should().Be(1);
        afterRemove.CoverFileIds.Should().ContainSingle().Which.Should().Be(files[3].Id);
    }

    [Fact]
    public async Task Album_member_browse_applies_search_filters_and_sorting_before_pagination()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var sunset = await UploadPhotoAsync(catalog, dbContext, ownerId, "sunset.jpg");
        var portrait = await UploadPhotoAsync(catalog, dbContext, ownerId, "portrait.mp4");
        var sunsetFile = await dbContext.Files.SingleAsync(file => file.Id == sunset.Id);
        var portraitFile = await dbContext.Files.SingleAsync(file => file.Id == portrait.Id);
        sunsetFile.CapturedAt = new DateTime(2024, 6, 15, 0, 0, 0, DateTimeKind.Utc);
        portraitFile.CapturedAt = new DateTime(2025, 2, 20, 0, 0, 0, DateTimeKind.Utc);
        portraitFile.MimeType = "video/mp4";
        var tag = new Tag
        {
            LibraryId = sunsetFile.LibraryId,
            Name = "Family",
            NormalizedName = "FAMILY",
            Color = "green",
            CreatedByUserId = ownerId
        };
        dbContext.Tags.Add(tag);
        dbContext.FileTags.Add(new FileTag { FileId = portrait.Id, TagId = tag.Id, AddedByUserId = ownerId });
        dbContext.FileUserStates.Add(new FileUserState { UserId = ownerId, FileId = sunset.Id, Favorite = true });
        await dbContext.SaveChangesAsync();

        var albums = new AlbumService(
            dbContext,
            new LibraryBootstrapper(dbContext),
            new TestDirectory(),
            NullLogger<AlbumService>.Instance);
        var album = (await albums.CreateAsync(ownerId, new CreateAlbumReq { Name = "Filtered" }, default)).SuccessValue;
        (await albums.SetItemsAsync(ownerId, album.Id, new SetAlbumItemsReq
        {
            ItemIds = [sunset.Id, portrait.Id],
            Included = true
        }, default)).IsSuccess.Should().BeTrue();

        var ordered = (await albums.GetAsync(ownerId, album.Id, new BrowseAlbumItemsReq
        {
            Sort = "captured_asc"
        }, default)).SuccessValue;
        ordered.Items.Select(item => item.Id).Should().Equal(sunset.Id, portrait.Id);
        ordered.AvailableYears.Should().Equal(2025, 2024);

        var favorites = (await albums.GetAsync(ownerId, album.Id, new BrowseAlbumItemsReq
        {
            Favorite = true
        }, default)).SuccessValue;
        favorites.Items.Should().ContainSingle(item => item.Id == sunset.Id);

        var filtered = (await albums.GetAsync(ownerId, album.Id, new BrowseAlbumItemsReq
        {
            Query = "family",
            Type = "video",
            Year = 2025,
            TagId = tag.Id
        }, default)).SuccessValue;
        filtered.Items.Should().ContainSingle(item => item.Id == portrait.Id)
            .Which.Tags.Should().ContainSingle(itemTag => itemTag.Id == tag.Id);
    }

    private static async Task<MediaItemRes> UploadPhotoAsync(MediaCatalogService catalog, MediaCatalogDbContext dbContext, Guid ownerId, string name)
    {
        var bytes = Encoding.UTF8.GetBytes($"test photo {name}");
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var upload = (await catalog.CreateUploadAsync(ownerId, new CreateUploadReq
        {
            Name = name,
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = true
        }, default)).SuccessValue;
        (await catalog.AppendUploadChunkAsync(ownerId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), default)).IsSuccess.Should().BeTrue();
        var file = (await catalog.CompleteUploadAsync(ownerId, upload.Id, new CompleteUploadReq { Sha256 = checksum }, default)).SuccessValue.Item;
        return file;
    }

    private sealed class TestDirectory(params ShareRecipientProfile[] profiles) : IShareRecipientDirectory
    {
        private readonly Dictionary<Guid, ShareRecipientProfile> _profiles = profiles.ToDictionary(profile => profile.UserId);

        public Task<IReadOnlyList<ShareRecipientProfile>> ListEligibleAsync(Guid requesterUserId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShareRecipientProfile>>(_profiles.Values.Where(profile => profile.UserId != requesterUserId).ToList());

        public Task<ShareRecipientProfile?> FindEligibleAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(_profiles.GetValueOrDefault(userId));

        public Task<IReadOnlyDictionary<Guid, ShareRecipientProfile>> FindByIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ShareRecipientProfile>>(_profiles.Where(pair => userIds.Contains(pair.Key)).ToDictionary());
    }
}
