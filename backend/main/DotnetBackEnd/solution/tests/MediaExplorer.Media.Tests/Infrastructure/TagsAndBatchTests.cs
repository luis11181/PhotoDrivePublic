using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class TagsAndBatchTests
{
    [Fact]
    public async Task Tags_are_case_insensitively_unique_and_flow_through_browse_filter_and_search()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var tags = new TagService(dbContext, new LibraryBootstrapper(dbContext));
        var folder = (await catalog.CreateFolderAsync(
            userId,
            new CreateFolderReq { Name = "Quarterly plans" },
            CancellationToken.None)).SuccessValue;

        var created = await tags.CreateAsync(
            userId,
            new CreateTagReq { Name = " Work ", Color = "blue" },
            CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        var duplicate = await tags.CreateAsync(
            userId,
            new CreateTagReq { Name = "work", Color = "red" },
            CancellationToken.None);
        duplicate.IsFailure.Should().BeTrue();
        duplicate.ErrorValue.ErrorCode.Should().Be(MediaErrors.ConflictCode);

        var assigned = await tags.SetItemsAsync(
            userId,
            created.SuccessValue.Id,
            new SetTagItemsReq { ItemIds = [folder.Id], Included = true },
            CancellationToken.None);
        assigned.IsSuccess.Should().BeTrue();
        assigned.SuccessValue.SucceededCount.Should().Be(1);

        var browse = await catalog.QueryFilesAsync(userId, new FileQueryOptions
        {
            Location = "children",
            Limit = 50,
            TagId = created.SuccessValue.Id
        }, CancellationToken.None);
        browse.IsSuccess.Should().BeTrue();
        browse.SuccessValue.Items.Should().ContainSingle(item =>
            item.Id == folder.Id
            && item.Tags.Count == 1
            && item.Tags[0].Name == "Work");

        var search = await catalog.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "work",
            Location = "all",
            Limit = 50
        }, CancellationToken.None);
        search.IsSuccess.Should().BeTrue();
        search.SuccessValue.Items.Should().ContainSingle(item => item.Id == folder.Id);

        var updated = await tags.UpdateAsync(
            userId,
            created.SuccessValue.Id,
            new UpdateTagReq { Name = "Projects", Color = "purple" },
            CancellationToken.None);
        updated.IsSuccess.Should().BeTrue();
        var renamedSearch = await catalog.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "projects",
            Location = "all",
            Limit = 50
        }, CancellationToken.None);
        renamedSearch.SuccessValue.Items.Should().ContainSingle(item => item.Id == folder.Id);

        (await tags.DeleteAsync(userId, created.SuccessValue.Id, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var deletedSearch = await catalog.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "projects",
            Location = "all",
            Limit = 50
        }, CancellationToken.None);
        deletedSearch.SuccessValue.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_trash_and_restore_skip_selected_descendants_covered_by_the_same_root()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var parent = (await catalog.CreateFolderAsync(
            userId,
            new CreateFolderReq { Name = "Parent" },
            CancellationToken.None)).SuccessValue;
        var child = (await catalog.CreateFolderAsync(
            userId,
            new CreateFolderReq { Name = "Child", ParentId = parent.Id },
            CancellationToken.None)).SuccessValue;
        var file = await UploadAsync(catalog, userId, child.Id);
        var batch = new CatalogBatchService(dbContext, new LibraryBootstrapper(dbContext), catalog);

        var trashed = await batch.ExecuteAsync(
            userId,
            new BatchItemsReq { Action = "trash", ItemIds = [parent.Id, child.Id, file.Id] },
            CancellationToken.None);
        trashed.IsSuccess.Should().BeTrue();
        trashed.SuccessValue.SucceededCount.Should().Be(1);
        trashed.SuccessValue.SkippedCount.Should().Be(2);
        (await dbContext.Folders.SingleAsync(item => item.Id == child.Id)).TrashedAt.Should().NotBeNull();
        (await dbContext.Files.SingleAsync(item => item.Id == file.Id)).TrashedAt.Should().NotBeNull();

        var restored = await batch.ExecuteAsync(
            userId,
            new BatchItemsReq { Action = "restore", ItemIds = [parent.Id, child.Id, file.Id] },
            CancellationToken.None);
        restored.IsSuccess.Should().BeTrue();
        restored.SuccessValue.SucceededCount.Should().Be(1);
        restored.SuccessValue.SkippedCount.Should().Be(2);
        (await dbContext.Folders.SingleAsync(item => item.Id == child.Id)).TrashedAt.Should().BeNull();
        (await dbContext.Files.SingleAsync(item => item.Id == file.Id)).TrashedAt.Should().BeNull();
    }

    [Fact]
    public async Task Capture_date_correction_updates_the_effective_projection_and_reset_restores_raw_precedence()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var file = await UploadAsync(catalog, userId, null);
        var stored = await dbContext.Files.SingleAsync(item => item.Id == file.Id);
        var metadata = await dbContext.FileVersionMetadata.SingleAsync(item => item.FileVersionId == stored.CurrentVersionId);
        metadata.ExifDateTimeOriginal = new DateTime(2019, 4, 5, 10, 30, 0, DateTimeKind.Utc);
        stored.CapturedAt = metadata.ExifDateTimeOriginal.Value;
        stored.CaptureDateSource = "exif_datetime_original";
        await dbContext.SaveChangesAsync();
        var batch = new CatalogBatchService(dbContext, new LibraryBootstrapper(dbContext), catalog);

        var corrected = await batch.SetCaptureDateAsync(
            userId,
            new SetCaptureDateReq { ItemIds = [file.Id], CapturedOn = new DateOnly(2001, 2, 3) },
            CancellationToken.None);

        corrected.IsSuccess.Should().BeTrue();
        corrected.SuccessValue.SucceededCount.Should().Be(1);
        stored.UserCapturedAt.Should().Be(new DateTime(2001, 2, 3, 12, 0, 0, DateTimeKind.Utc));
        stored.CapturedAt.Should().Be(stored.UserCapturedAt);
        stored.CaptureDateSource.Should().Be("user");
        metadata.EffectiveCapturedAt.Should().Be(stored.UserCapturedAt);

        var reset = await batch.SetCaptureDateAsync(
            userId,
            new SetCaptureDateReq { ItemIds = [file.Id], Reset = true },
            CancellationToken.None);

        reset.IsSuccess.Should().BeTrue();
        reset.SuccessValue.SucceededCount.Should().Be(1);
        stored.UserCapturedAt.Should().BeNull();
        stored.CapturedAt.Should().Be(metadata.ExifDateTimeOriginal);
        stored.CaptureDateSource.Should().Be("exif_datetime_original");
        metadata.CaptureDateSource.Should().Be("exif_datetime_original");
    }

    private static async Task<MediaItemRes> UploadAsync(
        MediaCatalogService catalog,
        Guid userId,
        Guid? parentId)
    {
        var bytes = Encoding.UTF8.GetBytes("batch file");
        var upload = (await catalog.CreateUploadAsync(
            userId,
            new CreateUploadReq
            {
                Name = "batch.txt",
                MimeType = "text/plain",
                SizeBytes = bytes.Length,
                ParentId = parentId
            },
            CancellationToken.None)).SuccessValue;
        (await catalog.AppendUploadChunkAsync(
            userId,
            upload.Id,
            0,
            bytes.Length - 1,
            bytes.Length,
            new MemoryStream(bytes),
            CancellationToken.None)).IsSuccess.Should().BeTrue();
        return (await catalog.CompleteUploadAsync(
            userId,
            upload.Id,
            new CompleteUploadReq
            {
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
            },
            CancellationToken.None)).SuccessValue.Item;
    }
}
