using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using FluentAssertions;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class MediaCatalogServiceTests
{
    [Fact]
    public async Task PrepareDownloadAsync_preserves_one_original_and_streams_multiple_as_a_zip()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);

        async Task<(MediaItemRes Item, byte[] Bytes)> UploadAsync(string name, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = name,
                MimeType = "text/plain",
                SizeBytes = bytes.Length
            }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
            var item = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);
            return (item, bytes);
        }

        var first = await UploadAsync("first note.txt", "first download");
        var second = await UploadAsync("second note.txt", "second download");

        var single = await service.PrepareDownloadAsync(userId, [first.Item.Id], CancellationToken.None);
        single.FileName.Should().Be("first note.txt");
        single.ContentType.Should().Be("text/plain");
        single.ContentLength.Should().Be(first.Bytes.Length);
        single.FileCount.Should().Be(1);
        single.TotalBytes.Should().Be(first.Bytes.Length);
        await using var singleBytes = new MemoryStream();
        await single.WriteToAsync(singleBytes, CancellationToken.None);
        singleBytes.ToArray().Should().Equal(first.Bytes);

        var multiple = await service.PrepareDownloadAsync(userId, [first.Item.Id, second.Item.Id], CancellationToken.None);
        multiple.FileName.Should().EndWith(".zip");
        multiple.ContentType.Should().Be("application/zip");
        multiple.ContentLength.Should().BeNull();
        multiple.FileCount.Should().Be(2);
        multiple.TotalBytes.Should().Be(first.Bytes.Length + second.Bytes.Length);
        var preview = await service.PreviewDownloadAsync(
            userId,
            [first.Item.Id, second.Item.Id],
            CancellationToken.None);
        preview.FileCount.Should().Be(2);
        preview.TotalBytes.Should().Be(first.Bytes.Length + second.Bytes.Length);
        await using var archiveBytes = new AsyncOnlyWriteStream();
        await multiple.WriteToAsync(archiveBytes, CancellationToken.None);
        archiveBytes.SynchronousWriteAttempts.Should().Be(0);
        using var archiveData = new MemoryStream(archiveBytes.ToArray());
        using var archive = new ZipArchive(archiveData, ZipArchiveMode.Read);
        archive.Entries.Select(entry => entry.FullName).Should().Equal("first note.txt", "second note.txt");
        await using var firstEntry = await archive.GetEntry("first note.txt")!.OpenAsync(CancellationToken.None);
        await using var secondEntry = await archive.GetEntry("second note.txt")!.OpenAsync(CancellationToken.None);
        using var firstReader = new StreamReader(firstEntry, Encoding.UTF8);
        using var secondReader = new StreamReader(secondEntry, Encoding.UTF8);
        (await firstReader.ReadToEndAsync()).Should().Be("first download");
        (await secondReader.ReadToEndAsync()).Should().Be("second download");
    }

    [Fact]
    public async Task Folder_download_streams_the_active_recursive_tree_with_safe_unique_paths_and_cancellation()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);

        var root = await service.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Family:Docs" }, default);
        var nested = await service.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Trips", ParentId = root.Id }, default);
        await service.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Empty", ParentId = root.Id }, default);

        async Task<MediaItemRes> UploadAsync(string name, Guid parentId, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var upload = await service.CreateUploadAsync(ownerId, new CreateUploadReq
            {
                Name = name,
                MimeType = "text/plain",
                SizeBytes = bytes.Length,
                ParentId = parentId,
                AllowNameConflict = true
            }, default);
            await service.AppendUploadChunkAsync(ownerId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), default);
            return await service.CompleteUploadAsync(ownerId, upload.Id, new CompleteUploadReq
            {
                Sha256 = Hash(bytes),
                AllowNameConflict = true
            }, default);
        }

        var selectedTwice = await UploadAsync("same.txt", root.Id, "first");
        await UploadAsync("same.txt", root.Id, "second");
        await UploadAsync("Trips", root.Id, "folder collision");
        await UploadAsync("nested.txt", nested.Id, "nested");
        var trashed = await UploadAsync("trashed.txt", nested.Id, "excluded");
        await service.TrashItemAsync(ownerId, trashed.Id, default);
        var documents = await dbContext.Folders.SingleAsync(folder => folder.SystemRole == LibraryBootstrapper.DocumentsFolderRole);
        var sibling = await UploadAsync("Family:Docs", documents.Id, "sibling file");

        var denied = await harness.CreateResultService(dbContext)
            .PrepareFolderDownloadAsync(strangerId, root.Id, default);
        denied.ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);

        var download = await service.PrepareFolderDownloadAsync(ownerId, root.Id, default);
        download.FileName.Should().Be("Family_Docs.zip");
        download.ContentType.Should().Be("application/zip");
        download.ContentLength.Should().BeNull();
        var preview = await service.PreviewFolderDownloadAsync(ownerId, root.Id, default);
        preview.FileCount.Should().Be(4);
        preview.TotalBytes.Should().Be(download.TotalBytes);
        await using var bytes = new AsyncOnlyWriteStream();
        await download.WriteToAsync(bytes, default);
        bytes.SynchronousWriteAttempts.Should().Be(0);

        using var archiveData = new MemoryStream(bytes.ToArray());
        using var archive = new ZipArchive(archiveData, ZipArchiveMode.Read);
        archive.Entries.Select(entry => entry.FullName).Should().BeEquivalentTo(
            "Family_Docs/",
            "Family_Docs/Trips/",
            "Family_Docs/Empty/",
            "Family_Docs/same.txt",
            "Family_Docs/same (2).txt",
            "Family_Docs/Trips (2)",
            "Family_Docs/Trips/nested.txt");
        archive.Entries.Should().NotContain(entry => entry.FullName.Contains("trashed", StringComparison.Ordinal));

        var mixed = await service.PrepareDownloadAsync(
            ownerId,
            [nested.Id, root.Id, selectedTwice.Id, sibling.Id],
            default);
        await using var mixedBytes = new AsyncOnlyWriteStream();
        await mixed.WriteToAsync(mixedBytes, default);
        using var mixedArchiveData = new MemoryStream(mixedBytes.ToArray());
        using var mixedArchive = new ZipArchive(mixedArchiveData, ZipArchiveMode.Read);
        mixedArchive.Entries.Select(entry => entry.FullName).Should().BeEquivalentTo(
            "Family_Docs/",
            "Family_Docs/Trips/",
            "Family_Docs/Empty/",
            "Family_Docs/same.txt",
            "Family_Docs/same (2).txt",
            "Family_Docs/Trips (2)",
            "Family_Docs/Trips/nested.txt",
            "Family_Docs (2)");
        mixedArchive.Entries.Count(entry => entry.FullName.EndsWith("nested.txt", StringComparison.Ordinal)).Should().Be(1);
        mixedArchive.Entries.Count(entry => entry.FullName.EndsWith("same.txt", StringComparison.Ordinal)).Should().Be(1);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await FluentActions.Invoking(() => download.WriteToAsync(new MemoryStream(), cancelled.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Album_download_streams_all_active_members_for_owner_or_current_viewer()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);

        async Task<MediaItemRes> UploadAsync(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var upload = await service.CreateUploadAsync(ownerId, new CreateUploadReq
            {
                Name = "photo?.jpg",
                MimeType = "image/jpeg",
                SizeBytes = bytes.Length,
                UseDefaultMediaFolder = true,
                AllowNameConflict = true
            }, default);
            await service.AppendUploadChunkAsync(ownerId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), default);
            return await service.CompleteUploadAsync(ownerId, upload.Id, new CompleteUploadReq
            {
                Sha256 = Hash(bytes),
                AllowNameConflict = true
            }, default);
        }

        var first = await UploadAsync("first album file");
        var second = await UploadAsync("second album file");
        var excluded = await UploadAsync("trashed album file");
        var library = await dbContext.Libraries.SingleAsync();
        var album = new Album { LibraryId = library.Id, Name = "Summer:2026" };
        dbContext.Albums.Add(album);
        dbContext.AlbumItems.AddRange(
            new AlbumItem { AlbumId = album.Id, FileId = first.Id, AddedByUserId = ownerId },
            new AlbumItem { AlbumId = album.Id, FileId = second.Id, AddedByUserId = ownerId },
            new AlbumItem { AlbumId = album.Id, FileId = excluded.Id, AddedByUserId = ownerId });
        dbContext.CatalogShares.Add(new CatalogShare
        {
            LibraryId = library.Id,
            AlbumId = album.Id,
            RecipientUserId = viewerId,
            GrantedByUserId = ownerId,
            Role = CatalogShare.ViewerRole
        });
        await dbContext.SaveChangesAsync();
        await service.TrashItemAsync(ownerId, excluded.Id, default);

        var resultService = harness.CreateResultService(dbContext);
        (await resultService.PrepareAlbumDownloadAsync(strangerId, album.Id, default))
            .ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
        var download = (await resultService.PrepareAlbumDownloadAsync(viewerId, album.Id, default)).SuccessValue;
        download.FileName.Should().Be("Summer_2026.zip");
        await using var bytes = new AsyncOnlyWriteStream();
        await download.WriteToAsync(bytes, default);

        using var archiveData = new MemoryStream(bytes.ToArray());
        using var archive = new ZipArchive(archiveData, ZipArchiveMode.Read);
        archive.Entries.Select(entry => entry.FullName).Should().BeEquivalentTo(
            "Summer_2026/",
            "Summer_2026/photo_.jpg",
            "Summer_2026/photo_ (2).jpg");
        archive.Entries.Should().NotContain(entry => entry.FullName.Contains("trashed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateFolderAsync_creates_catalog_metadata_without_object_storage()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);

        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "  Receipts  " }, CancellationToken.None);

        folder.Name.Should().Be("Receipts");
        folder.Kind.Should().Be("folder");
        harness.ObjectStoreFactory.PutCalls.Should().Be(0);
        var storedFolder = await dbContext.Folders.SingleAsync(candidate => candidate.Id == folder.Id);
        storedFolder.Name.Should().Be("Receipts");
        storedFolder.ParentFolderId.Should().Be(
            (await dbContext.Folders.SingleAsync(candidate => candidate.SystemRole == "documents_root")).Id);
        (await dbContext.Libraries.SingleAsync()).OwnerUserId.Should().Be(userId);
    }

    [Fact]
    public async Task CopyFileAsync_reuses_the_immutable_original_and_clones_catalog_metadata_without_Photos_or_favorite_state()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("copy me without storing me twice");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "notes.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length,
            UseDefaultDocumentsFolder = true
        }, default);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), default);
        var source = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, default);
        var library = await dbContext.Libraries.SingleAsync();
        var tag = new Tag
        {
            LibraryId = library.Id,
            Name = "Reference",
            NormalizedName = "REFERENCE",
            Color = "blue",
            CreatedByUserId = userId
        };
        dbContext.Tags.Add(tag);
        dbContext.FileTags.Add(new FileTag { FileId = source.Id, TagId = tag.Id, AddedByUserId = userId });
        await dbContext.SaveChangesAsync();
        var putCallsAfterUpload = harness.ObjectStoreFactory.PutCalls;

        var firstCopy = await service.CopyFileAsync(userId, source.Id, new CopyFileReq(), default);
        var secondCopy = await service.CopyFileAsync(userId, source.Id, new CopyFileReq(), default);

        firstCopy.Name.Should().Be("Copy of notes.txt");
        secondCopy.Name.Should().Be("Copy of notes (2).txt");
        firstCopy.ParentId.Should().Be(source.ParentId);
        firstCopy.Favorite.Should().BeFalse();
        firstCopy.Tags.Should().ContainSingle(item => item.Id == tag.Id);
        harness.ObjectStoreFactory.PutCalls.Should().Be(putCallsAfterUpload);
        (await dbContext.Blobs.CountAsync()).Should().Be(1);
        (await dbContext.FileVersions.Select(version => version.BlobSha256).Distinct().CountAsync()).Should().Be(1);
        (await dbContext.FileVersions.CountAsync()).Should().Be(3);
        (await dbContext.FileVersionMetadata.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task StorageSummaryAsync_deduplicates_originals_includes_trash_and_invalidates_after_storage_changes()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("one stored original shared by two catalog files");
        var checksum = Hash(bytes);

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);

        async Task<MediaItemRes> UploadAsync(string name)
        {
            var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = name,
                MimeType = "text/plain",
                SizeBytes = bytes.Length
            }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
            return await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = checksum }, CancellationToken.None);
        }

        var first = await UploadAsync("first.txt");
        var afterFirstUpload = await service.GetStorageSummaryAsync(userId, refresh: false, CancellationToken.None);
        afterFirstUpload.CatalogFileCount.Should().Be(1);
        afterFirstUpload.UniqueOriginalCount.Should().Be(1);
        afterFirstUpload.StoredOriginalBytes.Should().Be(bytes.Length);
        afterFirstUpload.ReclaimableTrashBytes.Should().Be(0);

        var second = await UploadAsync("second.txt");
        var afterDeduplicatedUpload = await service.GetStorageSummaryAsync(userId, refresh: false, CancellationToken.None);
        afterDeduplicatedUpload.CatalogFileCount.Should().Be(2);
        afterDeduplicatedUpload.UniqueOriginalCount.Should().Be(1);
        afterDeduplicatedUpload.StoredOriginalBytes.Should().Be(bytes.Length);

        await service.TrashItemAsync(userId, first.Id, CancellationToken.None);
        var whileSharedWithAnActiveFile = await service.GetStorageSummaryAsync(userId, refresh: false, CancellationToken.None);
        whileSharedWithAnActiveFile.StoredOriginalBytes.Should().Be(bytes.Length);
        whileSharedWithAnActiveFile.ReclaimableTrashOriginalCount.Should().Be(0);

        await service.TrashItemAsync(userId, second.Id, CancellationToken.None);
        var afterBothFilesAreTrashed = await service.GetStorageSummaryAsync(userId, refresh: false, CancellationToken.None);
        afterBothFilesAreTrashed.CatalogFileCount.Should().Be(2);
        afterBothFilesAreTrashed.StoredOriginalBytes.Should().Be(bytes.Length);
        afterBothFilesAreTrashed.ReclaimableTrashOriginalCount.Should().Be(1);
        afterBothFilesAreTrashed.ReclaimableTrashBytes.Should().Be(bytes.Length);

        await service.RestoreItemAsync(userId, second.Id, CancellationToken.None);
        var afterRestore = await service.GetStorageSummaryAsync(userId, refresh: false, CancellationToken.None);
        afterRestore.ReclaimableTrashOriginalCount.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_uses_the_catalog_fts_index_and_returns_a_catalog_path()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipts 2026" }, CancellationToken.None);

        var result = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "receipts",
            Location = "all"
        }, CancellationToken.None);

        result.Items.Should().ContainSingle(item => item.Id == folder.Id)
            .Which.CatalogPath.Should().Be("Documents / Receipts 2026");

        await service.SetFavoriteAsync(userId, folder.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);
        (await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "receipts",
            Location = "all",
            Favorite = true
        }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == folder.Id);

        await service.TrashItemAsync(userId, folder.Id, CancellationToken.None);
        (await service.SearchAsync(userId, "receipts", "trash", "folder", "all", null, 60, null, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == folder.Id)
            .Which.TrashedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task QueryFilesAsync_combines_recursive_location_favorite_and_text_before_paging()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var projects = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Projects" }, CancellationToken.None);
        var year = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "2026", ParentId = projects.Id }, CancellationToken.None);
        var nestedFavorite = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipt archive", ParentId = year.Id }, CancellationToken.None);
        var nestedNotFavorite = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipt drafts", ParentId = year.Id }, CancellationToken.None);
        var outsideFavorite = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipt personal" }, CancellationToken.None);
        await service.SetFavoriteAsync(userId, nestedFavorite.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);
        await service.SetFavoriteAsync(userId, outsideFavorite.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);

        var subtreeFavorites = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Location = "subtree",
            FolderId = projects.Id,
            Favorite = true,
            Limit = 100
        }, CancellationToken.None);
        subtreeFavorites.Items.Should().ContainSingle(item => item.Id == nestedFavorite.Id);
        subtreeFavorites.Items.Should().NotContain(item => item.Id == nestedNotFavorite.Id || item.Id == outsideFavorite.Id);
        subtreeFavorites.Items.Single().CatalogPath.Should().Be("Documents / Projects / 2026 / Receipt archive");

        var matchingSubtreeFavorites = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "receipt",
            Location = "subtree",
            FolderId = projects.Id,
            Favorite = true,
            Limit = 100
        }, CancellationToken.None);
        matchingSubtreeFavorites.Items.Should().ContainSingle(item => item.Id == nestedFavorite.Id);

        var allFavorites = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Location = "all",
            Favorite = true,
            Limit = 100
        }, CancellationToken.None);
        allFavorites.Items.Select(item => item.Id).Should().BeEquivalentTo([nestedFavorite.Id, outsideFavorite.Id]);
    }

    [Fact]
    public async Task QueryFilesAsync_applies_the_selected_sort_to_text_search_results()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var zulu = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Report zulu" }, CancellationToken.None);
        var alpha = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Report alpha" }, CancellationToken.None);

        var ascending = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "report",
            Location = "all",
            Sort = "name_asc",
            Limit = 100
        }, CancellationToken.None);
        ascending.Items.Select(item => item.Id).Should().Equal(alpha.Id, zulu.Id);

        var descending = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "report",
            Location = "all",
            Sort = "name_desc",
            Limit = 100
        }, CancellationToken.None);
        descending.Items.Select(item => item.Id).Should().Equal(zulu.Id, alpha.Id);
    }

    [Fact]
    public async Task SearchAsync_keeps_its_fts_index_in_sync_and_pages_without_repeating_results()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var folders = new[]
        {
            await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipt alpha" }, CancellationToken.None),
            await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipt beta" }, CancellationToken.None),
            await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipt gamma" }, CancellationToken.None)
        };

        var firstPage = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "receipt",
            Location = "all",
            Limit = 2
        }, CancellationToken.None);
        firstPage.NextCursor.Should().NotBeNull();
        var secondPage = await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "receipt",
            Location = "all",
            Cursor = firstPage.NextCursor,
            Limit = 2
        }, CancellationToken.None);

        firstPage.Items.Concat(secondPage.Items).Select(item => item.Id)
            .Should().BeEquivalentTo(folders.Select(folder => folder.Id));

        await service.RenameItemAsync(userId, folders[0].Id, new RenameItemReq { Name = "Archive 2026" }, CancellationToken.None);

        (await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "archive",
            Location = "all",
            Limit = 2
        }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == folders[0].Id);
        (await service.QueryFilesAsync(userId, new FileQueryOptions
        {
            Query = "alpha",
            Location = "all",
            Limit = 2
        }, CancellationToken.None)).Items
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Library_bootstrap_creates_protected_media_and_documents_folders()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var files = await service.QueryFilesAsync(userId, new FileQueryOptions { Limit = 100 }, CancellationToken.None);
        files.Items.Should().BeEmpty();

        var systemFolders = await dbContext.Folders
            .Where(item => item.SystemRole != null)
            .ToListAsync();
        systemFolders.Select(item => item.SystemRole)
            .Should().BeEquivalentTo("media_root", "documents_root");

        var media = systemFolders.Single(item => item.SystemRole == "media_root");
        await service.Invoking(candidate => candidate.RenameItemAsync(userId, media.Id, new RenameItemReq { Name = "Pictures" }, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*protected Media folder*");
        await service.Invoking(candidate => candidate.MoveItemAsync(userId, media.Id, new MoveItemReq(), CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*protected Media folder*");
        await service.Invoking(candidate => candidate.TrashItemAsync(userId, media.Id, CancellationToken.None))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*protected Media folder*");
    }

    [Fact]
    public async Task Files_children_query_uses_documents_workspace_without_flattened_paths()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Receipts" }, CancellationToken.None);
        var alphabeticFolder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Archive" }, CancellationToken.None);
        var files = await service.QueryFilesAsync(userId, new FileQueryOptions { Limit = 100 }, CancellationToken.None);

        files.Items.Should().ContainSingle(item => item.Id == folder.Id);
        files.Items.Select(item => item.Id).Should().ContainInOrder(alphabeticFolder.Id, folder.Id);
        files.Items.Should().NotContain(item => item.SystemRole != null);
        files.Items.Should().OnlyContain(item => item.CatalogPath == null);
    }

    [Fact]
    public async Task Files_browse_and_search_filter_by_relevant_file_types()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var fixtures = new[]
        {
            ("quarter-plan.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Encoding.UTF8.GetBytes("document")),
            ("quarter-budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Encoding.UTF8.GetBytes("spreadsheet")),
            ("quarter-receipt.pdf", "application/pdf", Encoding.UTF8.GetBytes("pdf")),
            ("quarter-photo.jpg", "image/jpeg", Encoding.UTF8.GetBytes("image"))
        };

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Quarterly" }, CancellationToken.None);
        foreach (var (name, mimeType, content) in fixtures)
        {
            var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = name,
                MimeType = mimeType,
                SizeBytes = content.Length
            }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, content.Length - 1, content.Length, new MemoryStream(content), CancellationToken.None);
            await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(content) }, CancellationToken.None);
        }

        (await service.QueryFilesAsync(userId, new FileQueryOptions { Type = "folder", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == folder.Id);
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Type = "document", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Name == "quarter-plan.docx");
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Type = "pdf", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Name == "quarter-receipt.pdf");
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Type = "spreadsheet", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Name == "quarter-budget.xlsx");
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Query = "quarter", Location = "all", Type = "pdf", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Name == "quarter-receipt.pdf");
    }

    [Fact]
    public async Task Pdf_type_filter_uses_the_filename_while_mime_type_normalization_is_pending()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var content = Encoding.UTF8.GetBytes("unprocessed PDF bytes");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "receipt.PDF",
            MimeType = "application/octet-stream",
            SizeBytes = content.Length
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, content.Length - 1, content.Length, new MemoryStream(content), CancellationToken.None);
        var file = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(content) }, CancellationToken.None);

        (await service.QueryFilesAsync(userId, new FileQueryOptions { Type = "pdf", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == file.Id);
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Query = "receipt", Location = "all", Type = "pdf", Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == file.Id);
    }

    [Fact]
    public async Task Photos_are_derived_from_media_location_and_all_scope_includes_media_elsewhere()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var imageBytes = MetadataFixtures.PngWithoutExif();
        var documentBytes = Encoding.UTF8.GetBytes("document bytes");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var imageUpload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "family.jpg",
            MimeType = "image/jpeg",
            SizeBytes = imageBytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, imageUpload.Id, 0, imageBytes.Length - 1, imageBytes.Length, new MemoryStream(imageBytes), CancellationToken.None);
        var image = await service.CompleteUploadAsync(userId, imageUpload.Id, new CompleteUploadReq { Sha256 = Hash(imageBytes) }, CancellationToken.None);

        var documentUpload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "notes.txt",
            MimeType = "text/plain",
            SizeBytes = documentBytes.Length,
            UseDefaultDocumentsFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, documentUpload.Id, 0, documentBytes.Length - 1, documentBytes.Length, new MemoryStream(documentBytes), CancellationToken.None);
        var document = await service.CompleteUploadAsync(userId, documentUpload.Id, new CompleteUploadReq { Sha256 = Hash(documentBytes) }, CancellationToken.None);
        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);
        image = await service.GetItemAsync(userId, image.Id, CancellationToken.None);
        document = await service.GetItemAsync(userId, document.Id, CancellationToken.None);

        var mediaFolder = await dbContext.Folders.SingleAsync(folder => folder.SystemRole == "media_root");
        var documentsFolder = await dbContext.Folders.SingleAsync(folder => folder.SystemRole == "documents_root");
        image.ParentId.Should().Be(mediaFolder.Id);
        document.ParentId.Should().Be(documentsFolder.Id);
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Query = "family", Location = "all", Limit = 100 }, CancellationToken.None)).Items
            .Should().BeEmpty();
        (await service.QueryFilesAsync(userId, new FileQueryOptions { Query = "family", Location = "all", IncludePhotos = true, Limit = 100 }, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == image.Id);
        (await service.SearchAsync(userId, "family", "photos", "folder", "all", null, 100, null, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == image.Id);
        (await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == image.Id);

        var trips = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Trips" }, CancellationToken.None);
        await service.MoveItemAsync(userId, image.Id, new MoveItemReq { ParentId = trips.Id }, CancellationToken.None);
        (await service.BrowsePhotosAsync(userId, "folder", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None)).Items
            .Should().BeEmpty();
        (await service.BrowsePhotosAsync(userId, "all", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == image.Id);
        (await service.SearchAsync(userId, "family", "photos", "all", "all", null, 100, null, CancellationToken.None)).Items
            .Should().ContainSingle(item => item.Id == image.Id);
    }

    [Fact]
    public async Task Photo_timeline_anchors_count_filtered_months_and_seek_without_loading_preceding_pages()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var imageBytes = MetadataFixtures.PngWithoutExif();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var uploaded = new List<MediaItemRes>();
        for (var index = 0; index < 4; index++)
        {
            var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = $"timeline-{index}.png",
                MimeType = "image/png",
                SizeBytes = imageBytes.Length,
                UseDefaultMediaFolder = true
            }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, imageBytes.Length - 1, imageBytes.Length, new MemoryStream(imageBytes), CancellationToken.None);
            uploaded.Add(await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(imageBytes) }, CancellationToken.None));
        }
        await harness.CreateMetadataProcessor(dbContext).ProcessPendingBatchAsync(10, CancellationToken.None);

        var captureDates = new[]
        {
            new DateTime(2026, 2, 14, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 14, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 11, 3, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 3, 20, 18, 0, 0, DateTimeKind.Utc)
        };
        var uploadedIds = uploaded.Select(item => item.Id).ToArray();
        var storedFiles = await dbContext.Files.Where(file => uploadedIds.Contains(file.Id)).ToListAsync();
        foreach (var file in storedFiles)
            file.CapturedAt = captureDates[Array.IndexOf(uploadedIds, file.Id)];
        await dbContext.SaveChangesAsync();
        await service.SetFavoriteAsync(userId, uploaded[2].Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);

        var anchors = await service.BrowsePhotoTimelineAnchorsAsync(
            userId, "folder", null, "all", false, "captured_desc", null, null, null, CancellationToken.None);
        anchors.TotalCount.Should().Be(4);
        anchors.Anchors.Select(anchor => (anchor.Year, anchor.Month, anchor.Count)).Should().Equal(
            (2026, 2, 2),
            (2025, 11, 1),
            (2024, 3, 1));
        anchors.Anchors.Should().OnlyContain(anchor => !string.IsNullOrWhiteSpace(anchor.Cursor));

        var novemberPage = await service.BrowsePhotosAsync(
            userId, "folder", null, anchors.Anchors[1].Cursor, 2, "all", false, "captured_desc", null, null, CancellationToken.None);
        novemberPage.Items.Select(item => item.Id).Should().Equal(uploaded[2].Id, uploaded[3].Id);
        novemberPage.PreviousCursor.Should().NotBeNull();
        var newerThanNovember = await service.BrowsePhotosAsync(
            userId, "folder", null, novemberPage.PreviousCursor, 2, "all", false, "captured_desc", null, null, CancellationToken.None);
        newerThanNovember.Items.Select(item => item.Id).Should().Equal(uploaded[1].Id, uploaded[0].Id);
        newerThanNovember.PreviousCursor.Should().BeNull();
        newerThanNovember.NextCursor.Should().NotBeNull();
        var backTowardNovember = await service.BrowsePhotosAsync(
            userId, "folder", null, newerThanNovember.NextCursor, 2, "all", false, "captured_desc", null, null, CancellationToken.None);
        backTowardNovember.Items.Select(item => item.Id).Should().Equal(uploaded[2].Id, uploaded[3].Id);

        var firstPage = await service.BrowsePhotosAsync(
            userId, "folder", null, anchors.Anchors[0].Cursor, 1, "all", false, "captured_desc", null, null, CancellationToken.None);
        firstPage.NextCursor.Should().NotBeNull();
        var secondPage = await service.BrowsePhotosAsync(
            userId, "folder", null, firstPage.NextCursor, 1, "all", false, "captured_desc", null, null, CancellationToken.None);
        secondPage.Items.Should().ContainSingle();
        secondPage.Items[0].Id.Should().NotBe(firstPage.Items[0].Id);
        secondPage.Items[0].CapturedAt.Should().Be(firstPage.Items[0].CapturedAt);

        var favoriteAnchors = await service.BrowsePhotoTimelineAnchorsAsync(
            userId, "folder", null, "all", true, "captured_desc", null, null, null, CancellationToken.None);
        favoriteAnchors.TotalCount.Should().Be(1);
        favoriteAnchors.Anchors.Should().ContainSingle()
            .Which.Should().Match<PhotoTimelineAnchorRes>(anchor => anchor.Year == 2025 && anchor.Month == 11 && anchor.Count == 1);

        var ascendingAnchors = await service.BrowsePhotoTimelineAnchorsAsync(
            userId, "folder", null, "all", false, "captured_asc", null, null, null, CancellationToken.None);
        ascendingAnchors.Anchors.Select(anchor => (anchor.Year, anchor.Month)).Should().Equal(
            (2024, 3),
            (2025, 11),
            (2026, 2));
        var ascendingNovember = await service.BrowsePhotosAsync(
            userId, "folder", null, ascendingAnchors.Anchors[1].Cursor, 2, "all", false, "captured_asc", null, null, CancellationToken.None);
        ascendingNovember.Items.Select(item => item.Id).Should().Equal(uploaded[2].Id, uploaded[0].Id);
        ascendingNovember.PreviousCursor.Should().NotBeNull();
        var olderThanNovember = await service.BrowsePhotosAsync(
            userId, "folder", null, ascendingNovember.PreviousCursor, 2, "all", false, "captured_asc", null, null, CancellationToken.None);
        olderThanNovember.Items.Select(item => item.Id).Should().Equal(uploaded[3].Id);
        olderThanNovember.NextCursor.Should().NotBeNull();

        var recentlyAddedFirst = await service.BrowsePhotosAsync(
            userId, "folder", null, null, 1, "all", false, "added_desc", null, null, CancellationToken.None);
        var recentlyAddedSecond = await service.BrowsePhotosAsync(
            userId, "folder", null, recentlyAddedFirst.NextCursor, 1, "all", false, "added_desc", null, null, CancellationToken.None);
        recentlyAddedSecond.Items.Should().ContainSingle();
        recentlyAddedSecond.Items[0].Id.Should().NotBe(recentlyAddedFirst.Items[0].Id);
    }

    [Fact]
    public async Task AppendUploadChunkAsync_rejects_an_upload_owned_by_another_user()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(ownerId, new CreateUploadReq
        {
            Name = "private.txt",
            MimeType = "text/plain",
            SizeBytes = 3
        }, CancellationToken.None);

        var action = () => service.AppendUploadChunkAsync(otherUserId, upload.Id, 0, 2, 3,
            new MemoryStream(Encoding.UTF8.GetBytes("bad")), CancellationToken.None);

        await action.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*not found*");
        (await dbContext.UploadSessions.SingleAsync()).ReceivedBytes.Should().Be(0);
    }

    [Fact]
    public async Task RenameAndMove_folder_updates_its_catalog_metadata_and_rejects_cycles()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var parent = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Parent" }, CancellationToken.None);
        var child = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Child", ParentId = parent.Id }, CancellationToken.None);

        var renamed = await service.RenameItemAsync(userId, child.Id, new RenameItemReq { Name = " Renamed child " }, CancellationToken.None);
        renamed.Name.Should().Be("Renamed child");

        var cycle = () => service.MoveItemAsync(userId, parent.Id, new MoveItemReq { ParentId = child.Id }, CancellationToken.None);
        await cycle.Should().ThrowAsync<ArgumentException>().WithMessage("*descendants*");

        var moved = await service.MoveItemAsync(userId, child.Id, new MoveItemReq { ParentId = null }, CancellationToken.None);
        moved.ParentId.Should().Be(
            (await dbContext.Folders.SingleAsync(candidate => candidate.SystemRole == "documents_root")).Id);
        (await service.GetItemAsync(userId, child.Id, CancellationToken.None)).Name.Should().Be("Renamed child");
    }

    [Fact]
    public async Task SetFavoriteAsync_is_user_scoped_and_is_returned_in_file_queries()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var upload = await CreateAndFillUploadAsync(harness, ownerId, Encoding.UTF8.GetBytes("favorite"));
        MediaItemRes file;

        await using (var dbContext = harness.CreateDbContext())
        {
            var service = harness.CreateService(dbContext);
            var folder = await service.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Favorite folder" }, CancellationToken.None);
            var nestedFolder = await service.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Nested favorite", ParentId = folder.Id }, CancellationToken.None);
            file = await service.CompleteUploadAsync(ownerId, upload.Id, new CompleteUploadReq { Sha256 = Hash(Encoding.UTF8.GetBytes("favorite")) }, CancellationToken.None);
            var favorite = await service.SetFavoriteAsync(ownerId, file.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);
            favorite.Favorite.Should().BeTrue();
            var folderFavorite = await service.SetFavoriteAsync(ownerId, folder.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);
            folderFavorite.Favorite.Should().BeTrue();
            await service.SetFavoriteAsync(ownerId, nestedFolder.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);
            (await service.QueryFilesAsync(ownerId, new FileQueryOptions { Limit = 100 }, CancellationToken.None)).Items.Single(item => item.Id == file.Id).Favorite.Should().BeTrue();
            (await service.QueryFilesAsync(ownerId, new FileQueryOptions { Limit = 100 }, CancellationToken.None)).Items.Single(item => item.Id == folder.Id).Favorite.Should().BeTrue();
            var rootFavorites = (await service.QueryFilesAsync(ownerId, new FileQueryOptions { Favorite = true, Limit = 100 }, CancellationToken.None)).Items;
            rootFavorites.Should().Contain(item => item.Id == file.Id);
            rootFavorites.Should().Contain(item => item.Id == folder.Id);
            rootFavorites.Should().NotContain(item => item.Id == nestedFolder.Id);
            var nestedFavorites = (await service.QueryFilesAsync(ownerId, new FileQueryOptions { FolderId = folder.Id, Favorite = true, Limit = 100 }, CancellationToken.None)).Items;
            nestedFavorites.Should().ContainSingle(item => item.Id == nestedFolder.Id);
        }

        await using (var otherContext = harness.CreateDbContext())
        {
            var otherService = harness.CreateService(otherContext);
            var action = () => otherService.SetFavoriteAsync(otherUserId, file.Id, new SetFavoriteReq { Favorite = true }, CancellationToken.None);
            await action.Should().ThrowAsync<KeyNotFoundException>();
        }
    }

    [Fact]
    public async Task TrashAndRestore_folder_hides_and_restores_its_descendant_files_until_manually_cleared()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("inside the folder");
        MediaItemRes folder;
        MediaItemRes file;

        await using (var dbContext = harness.CreateDbContext())
        {
            var service = harness.CreateService(dbContext);
            folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "Trips" }, CancellationToken.None);
            var created = await service.CreateUploadAsync(userId, new CreateUploadReq { Name = "photo.txt", MimeType = "text/plain", SizeBytes = bytes.Length, ParentId = folder.Id }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, created.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
            file = await service.CompleteUploadAsync(userId, created.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);

            var trashed = await service.TrashItemAsync(userId, folder.Id, CancellationToken.None);
            trashed.TrashedAt.Should().NotBeNull();
            (await service.QueryFilesAsync(userId, new FileQueryOptions { Limit = 100 }, CancellationToken.None)).Items.Should().BeEmpty();
            (await service.BrowseTrashAsync(userId, null, 100, "all", "name_asc", CancellationToken.None)).Items.Should().ContainSingle(item => item.Id == folder.Id);
            (await service.GetItemAsync(userId, file.Id, CancellationToken.None)).TrashedAt.Should().NotBeNull();

            var restored = await service.RestoreItemAsync(userId, folder.Id, CancellationToken.None);
            restored.TrashedAt.Should().BeNull();
            (await service.GetItemAsync(userId, file.Id, CancellationToken.None)).TrashedAt.Should().BeNull();
        }
    }

    [Fact]
    public async Task ClearTrashAsync_permanently_removes_unshared_original_but_preserves_active_original()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("permanently remove this catalog item");
        var activeBytes = Encoding.UTF8.GetBytes("keep this active catalog item");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var folder = await service.CreateFolderAsync(userId, new CreateFolderReq { Name = "To remove" }, CancellationToken.None);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "gone.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length,
            ParentId = folder.Id
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var file = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);
        var activeUpload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "keep.txt",
            MimeType = "text/plain",
            SizeBytes = activeBytes.Length,
            UseDefaultDocumentsFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, activeUpload.Id, 0, activeBytes.Length - 1, activeBytes.Length, new MemoryStream(activeBytes), CancellationToken.None);
        var activeFile = await service.CompleteUploadAsync(userId, activeUpload.Id, new CompleteUploadReq { Sha256 = Hash(activeBytes) }, CancellationToken.None);
        var cache = new OriginalCache(harness.Configuration);
        await cache.EnsureAsync(Hash(bytes), bytes.Length, (destination, token) => destination.WriteAsync(bytes, token).AsTask(), CancellationToken.None);
        var thumbnailDirectory = Path.Combine(harness.RootDirectory, "thumbnails", "sha256", Hash(bytes));
        Directory.CreateDirectory(thumbnailDirectory);
        await File.WriteAllBytesAsync(Path.Combine(thumbnailDirectory, "320.webp"), [1, 2, 3]);
        await service.TrashItemAsync(userId, folder.Id, CancellationToken.None);

        var rejected = await harness.CreateResultService(dbContext).ClearTrashAsync(userId, new ClearTrashReq(), CancellationToken.None);
        rejected.IsFailure.Should().BeTrue();

        var summary = await service.GetTrashSummaryAsync(userId, CancellationToken.None);
        summary.FileCount.Should().Be(1);
        summary.FolderCount.Should().Be(1);
        summary.OriginalCount.Should().Be(1);
        summary.OriginalBytes.Should().Be(bytes.Length);
        var staleCount = await harness.CreateResultService(dbContext).ClearTrashAsync(userId, new ClearTrashReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE",
            ExpectedFileCount = summary.FileCount + 1,
            ExpectedFolderCount = summary.FolderCount,
            ExpectedOriginalCount = summary.OriginalCount,
            ExpectedOriginalBytes = summary.OriginalBytes
        }, CancellationToken.None);
        staleCount.IsFailure.Should().BeTrue();
        (await dbContext.Files.AnyAsync(candidate => candidate.Id == file.Id)).Should().BeTrue();

        var result = await service.ClearTrashAsync(userId, new ClearTrashReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE",
            ExpectedFileCount = summary.FileCount,
            ExpectedFolderCount = summary.FolderCount,
            ExpectedOriginalCount = summary.OriginalCount,
            ExpectedOriginalBytes = summary.OriginalBytes
        }, CancellationToken.None);

        result.DeletedFolderCount.Should().Be(1);
        result.DeletedFileCount.Should().Be(1);
        result.DeletedOriginalCount.Should().Be(1);
        result.DeletedOriginalBytes.Should().Be(bytes.Length);
        result.FailedOriginalCount.Should().Be(0);
        (await service.BrowseTrashAsync(userId, null, 100, "all", "name_asc", CancellationToken.None)).Items.Should().BeEmpty();
        (await dbContext.Files.AnyAsync(candidate => candidate.Id == file.Id)).Should().BeFalse();
        (await dbContext.Files.AnyAsync(candidate => candidate.Id == activeFile.Id)).Should().BeTrue();
        (await dbContext.FileVersions.CountAsync()).Should().Be(1);
        (await dbContext.Blobs.CountAsync()).Should().Be(1);
        harness.ObjectStoreFactory.Originals.Should().NotContainKey(Hash(bytes));
        harness.ObjectStoreFactory.Originals.Should().ContainKey(Hash(activeBytes));
        harness.ObjectStoreFactory.ReconciliationInventoryCalls.Should().Be(0);
        File.Exists(cache.PathFor(Hash(bytes))).Should().BeFalse();
        Directory.Exists(thumbnailDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task ClearTrashAsync_never_deletes_an_original_referenced_by_an_active_deduplicated_file()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("shared original");
        var hash = Hash(bytes);

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var files = new List<MediaItemRes>();
        foreach (var name in new[] { "trash-me.txt", "keep-me.txt" })
        {
            var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = name,
                MimeType = "text/plain",
                SizeBytes = bytes.Length,
                UseDefaultDocumentsFolder = true
            }, CancellationToken.None);
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
            files.Add(await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None));
        }
        await service.TrashItemAsync(userId, files[0].Id, CancellationToken.None);

        var summary = await service.GetTrashSummaryAsync(userId, CancellationToken.None);
        summary.FileCount.Should().Be(1);
        summary.OriginalCount.Should().Be(0);
        var result = await service.ClearTrashAsync(userId, new ClearTrashReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE",
            ExpectedFileCount = summary.FileCount,
            ExpectedFolderCount = summary.FolderCount,
            ExpectedOriginalCount = summary.OriginalCount,
            ExpectedOriginalBytes = summary.OriginalBytes
        }, CancellationToken.None);

        result.DeletedFileCount.Should().Be(1);
        result.DeletedOriginalCount.Should().Be(0);
        (await dbContext.Files.AnyAsync(file => file.Id == files[1].Id)).Should().BeTrue();
        (await dbContext.Blobs.CountAsync()).Should().Be(1);
        harness.ObjectStoreFactory.Originals.Should().ContainKey(hash);
        harness.ObjectStoreFactory.PermanentDeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task ClearTrashAsync_sends_failed_storage_deletion_to_developer_maintenance_not_a_later_trash_clear()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("retry permanent deletion");
        var hash = Hash(bytes);

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "retry.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length,
            UseDefaultDocumentsFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var file = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = hash }, CancellationToken.None);
        await service.TrashItemAsync(userId, file.Id, CancellationToken.None);

        var summary = await service.GetTrashSummaryAsync(userId, CancellationToken.None);
        harness.ObjectStoreFactory.FailNextPermanentDelete = true;
        var first = await service.ClearTrashAsync(userId, new ClearTrashReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE",
            ExpectedFileCount = summary.FileCount,
            ExpectedFolderCount = summary.FolderCount,
            ExpectedOriginalCount = summary.OriginalCount,
            ExpectedOriginalBytes = summary.OriginalBytes
        }, CancellationToken.None);

        first.FailedOriginalCount.Should().Be(1);
        harness.ObjectStoreFactory.Originals.Should().ContainKey(hash);
        (await dbContext.Blobs.SingleAsync()).LifecycleState.Should().Be(Blob.PendingPermanentDeletionLifecycleState);

        var postTrashSummary = await service.GetTrashSummaryAsync(userId, CancellationToken.None);
        postTrashSummary.FileCount.Should().Be(0);
        postTrashSummary.OriginalCount.Should().Be(0);

        var reconciliation = new StorageReconciliationService(dbContext, harness.ObjectStoreFactory, new OriginalCache(harness.Configuration), harness.Configuration, NullLogger<StorageReconciliationService>.Instance);
        var report = await reconciliation.ReconcileAsync(CancellationToken.None);
        report.CleanupCandidates.Should().ContainSingle(candidate => candidate.Kind == StorageCleanupCandidate.OrphanedCatalogBlobKind);
        var candidate = report.CleanupCandidates.Single();
        var cleanup = await reconciliation.CleanupAsync(new CleanupStorageInconsistenciesReq
        {
            Confirmed = true,
            ConfirmationPhrase = "DELETE INCONSISTENCIES",
            ExpectedCandidateCount = 1,
            CandidateIds = [candidate.Id]
        }, "verified-test-backup", CancellationToken.None);

        cleanup.IsSuccess.Should().BeTrue();
        cleanup.SuccessValue.DeletedCandidateCount.Should().Be(1);
        cleanup.SuccessValue.FailedCandidateCount.Should().Be(0);
        (await dbContext.Blobs.AnyAsync()).Should().BeFalse();
        harness.ObjectStoreFactory.Originals.Should().NotContainKey(hash);
    }

    [Fact]
    public async Task Trash_browse_returns_an_individually_trashed_file_from_a_protected_folder_as_a_root()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("trash root file");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "trashed.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length,
            UseDefaultDocumentsFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var file = await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);

        await service.TrashItemAsync(userId, file.Id, CancellationToken.None);

        var trash = await service.BrowseTrashAsync(userId, null, 100, "all", "name_asc", CancellationToken.None);
        trash.Items.Should().ContainSingle(item => item.Id == file.Id);
    }

    [Fact]
    public async Task Trash_thumbnail_remains_available_to_its_owner_without_exposing_original_content()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("image bytes are not decoded when the thumbnail is already cached");
        var checksum = Hash(bytes);
        var cachedThumbnail = new byte[] { 1, 2, 3, 4 };

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var upload = await service.CreateUploadAsync(ownerId, new CreateUploadReq
        {
            Name = "trashed.jpg",
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(ownerId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var file = await service.CompleteUploadAsync(ownerId, upload.Id, new CompleteUploadReq { Sha256 = checksum }, CancellationToken.None);
        var thumbnailDirectory = Path.Combine(harness.RootDirectory, "thumbnails", "sha256", checksum);
        Directory.CreateDirectory(thumbnailDirectory);
        await File.WriteAllBytesAsync(Path.Combine(thumbnailDirectory, "320.webp"), cachedThumbnail);

        await service.TrashItemAsync(ownerId, file.Id, CancellationToken.None);

        using var thumbnail = await service.OpenThumbnailAsync(ownerId, file.Id, "md", CancellationToken.None);
        await using var thumbnailBytes = new MemoryStream();
        await thumbnail.Content.CopyToAsync(thumbnailBytes);
        thumbnailBytes.ToArray().Should().Equal(cachedThumbnail);

        var resultService = harness.CreateResultService(dbContext);
        (await resultService.OpenContentAsync(ownerId, file.Id, null, null, CancellationToken.None))
            .ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
        (await resultService.OpenThumbnailAsync(otherUserId, file.Id, "md", CancellationToken.None))
            .ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
    }

    [Fact]
    public async Task Upload_name_conflicts_are_reported_and_require_explicit_keep_both()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("same visible name");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);
        var firstUpload = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "camera.jpg",
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        firstUpload.IsSuccess.Should().BeTrue();
        await service.AppendUploadChunkAsync(userId, firstUpload.SuccessValue.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        var first = await service.CompleteUploadAsync(userId, firstUpload.SuccessValue.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        var conflicts = await service.CheckNameConflictsAsync(userId, new CheckUploadNameConflictsReq
        {
            Names = ["camera.jpg"],
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        conflicts.IsSuccess.Should().BeTrue();
        conflicts.SuccessValue.Names.Should().ContainSingle().Which.Should().Be("camera.jpg");

        var rejected = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "camera.jpg",
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        rejected.IsFailure.Should().BeTrue();
        rejected.ErrorValue.ErrorCode.Should().Be(MediaErrors.ConflictCode);
        rejected.ErrorValue.Message.Should().Be("An item with this name already exists.");

        var allowed = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "camera.jpg",
            MimeType = "image/jpeg",
            SizeBytes = bytes.Length,
            UseDefaultMediaFolder = true,
            AllowNameConflict = true
        }, CancellationToken.None);
        allowed.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Upload_completion_rechecks_late_name_conflicts_without_retransmitting_bytes()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var firstBytes = Encoding.UTF8.GetBytes("first camera file");
        var secondBytes = Encoding.UTF8.GetBytes("second camera file");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);
        var first = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "camera.jpg",
            MimeType = "image/jpeg",
            SizeBytes = firstBytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        var second = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "camera.jpg",
            MimeType = "image/jpeg",
            SizeBytes = secondBytes.Length,
            UseDefaultMediaFolder = true
        }, CancellationToken.None);
        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        await service.AppendUploadChunkAsync(userId, first.SuccessValue.Id, 0, firstBytes.Length - 1, firstBytes.Length, new MemoryStream(firstBytes), CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, second.SuccessValue.Id, 0, secondBytes.Length - 1, secondBytes.Length, new MemoryStream(secondBytes), CancellationToken.None);

        (await service.CompleteUploadAsync(userId, first.SuccessValue.Id, new CompleteUploadReq
        {
            Sha256 = Hash(firstBytes)
        }, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var conflict = await service.CompleteUploadAsync(userId, second.SuccessValue.Id, new CompleteUploadReq
        {
            Sha256 = Hash(secondBytes)
        }, CancellationToken.None);
        conflict.IsFailure.Should().BeTrue();
        conflict.ErrorValue.ErrorCode.Should().Be(MediaErrors.ConflictCode);

        var keptBoth = await service.CompleteUploadAsync(userId, second.SuccessValue.Id, new CompleteUploadReq
        {
            Sha256 = Hash(secondBytes),
            AllowNameConflict = true
        }, CancellationToken.None);
        keptBoth.IsSuccess.Should().BeTrue();
        (await dbContext.Files.CountAsync(file => file.Name == "camera.jpg")).Should().Be(2);
        harness.ObjectStoreFactory.PutCalls.Should().Be(2);
    }

    [Fact]
    public async Task Upload_completion_reports_same_library_duplicate_originals_explicitly()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("same immutable original");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);

        async Task<CompleteUploadRes> UploadAsync(string name)
        {
            var upload = (await service.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = name,
                MimeType = "text/plain",
                SizeBytes = bytes.Length
            }, CancellationToken.None)).SuccessValue;
            await service.AppendUploadChunkAsync(userId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
            return (await service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq
            {
                Sha256 = Hash(bytes)
            }, CancellationToken.None)).SuccessValue;
        }

        (await UploadAsync("first.txt")).DuplicateState.Should().Be("none");
        var duplicate = await UploadAsync("second.txt");
        duplicate.DuplicateState.Should().Be("active");
        duplicate.Item.Name.Should().Be("second.txt");
        harness.ObjectStoreFactory.Originals.Should().ContainSingle();
    }

    [Fact]
    public async Task Upload_status_is_owner_scoped_and_cancellation_removes_pending_staging()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var clientUploadId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("resume then cancel");

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);
        var created = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            ClientUploadId = clientUploadId,
            Name = "cancel.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        created.IsSuccess.Should().BeTrue();
        await service.AppendUploadChunkAsync(
            userId,
            created.SuccessValue.Id,
            0,
            5,
            bytes.Length,
            new MemoryStream(bytes[..6]),
            CancellationToken.None);
        var idempotentCreate = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            ClientUploadId = clientUploadId,
            Name = "cancel.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        idempotentCreate.IsSuccess.Should().BeTrue();
        idempotentCreate.SuccessValue.Id.Should().Be(created.SuccessValue.Id);
        idempotentCreate.SuccessValue.ReceivedBytes.Should().Be(6);

        var status = await service.GetUploadSessionAsync(userId, created.SuccessValue.Id, CancellationToken.None);
        status.IsSuccess.Should().BeTrue();
        status.SuccessValue.ReceivedBytes.Should().Be(6);
        status.SuccessValue.SizeBytes.Should().Be(bytes.Length);
        status.SuccessValue.State.Should().Be("pending");
        (await service.GetUploadSessionAsync(Guid.NewGuid(), created.SuccessValue.Id, CancellationToken.None))
            .ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);

        var stagingFileName = (await dbContext.UploadSessions.SingleAsync()).StagingFileName;
        File.Exists(Path.Combine(harness.StagingDirectory, stagingFileName)).Should().BeTrue();
        (await service.CancelUploadAsync(userId, created.SuccessValue.Id, CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await dbContext.UploadSessions.CountAsync()).Should().Be(0);
        File.Exists(Path.Combine(harness.StagingDirectory, stagingFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task Upload_can_resume_in_a_new_service_scope_and_complete_once()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("a resumable upload survives a service restart");
        var checksum = Hash(bytes);
        CreateUploadRes upload;

        await using (var firstContext = harness.CreateDbContext())
        {
            var firstService = harness.CreateService(firstContext);
            upload = await firstService.CreateUploadAsync(userId, new CreateUploadReq
            {
                Name = "resume.txt",
                MimeType = "text/plain",
                SizeBytes = bytes.Length
            }, CancellationToken.None);

            var firstChunk = bytes[..12];
            var progress = await firstService.AppendUploadChunkAsync(userId, upload.Id, 0, firstChunk.Length - 1, bytes.Length,
                new MemoryStream(firstChunk), CancellationToken.None);
            progress.ReceivedBytes.Should().Be(firstChunk.Length);
        }

        await using (var resumedContext = harness.CreateDbContext())
        {
            var resumedService = harness.CreateService(resumedContext);
            var remaining = bytes[12..];
            var progress = await resumedService.AppendUploadChunkAsync(userId, upload.Id, 12, bytes.Length - 1, bytes.Length,
                new MemoryStream(remaining), CancellationToken.None);
            progress.ReceivedBytes.Should().Be(bytes.Length);

            var file = await resumedService.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = checksum }, CancellationToken.None);
            await harness.CreateMetadataProcessor(resumedContext).ProcessPendingBatchAsync(10, CancellationToken.None);
            file.Name.Should().Be("resume.txt");
            file.SizeBytes.Should().Be(bytes.Length);
        }

        harness.ObjectStoreFactory.Originals.Should().ContainSingle().Which.Value.Should().Equal(bytes);
        harness.ObjectStoreFactory.PutCalls.Should().Be(1);
        await using var verificationContext = harness.CreateDbContext();
        var completedUpload = await verificationContext.UploadSessions.SingleAsync(candidate => candidate.Id == upload.Id);
        completedUpload.State.Should().Be("completed");
        File.Exists(Path.Combine(harness.StagingDirectory, completedUpload.StagingFileName)).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteUploadAsync_rejects_a_checksum_mismatch_and_keeps_staging_for_retry()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("checksum mismatch");
        var upload = await CreateAndFillUploadAsync(harness, userId, bytes);

        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var action = () => service.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = new string('0', 64) }, CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentException>().WithMessage("*did not match*");
        (await dbContext.UploadSessions.SingleAsync()).State.Should().Be("pending");
        File.Exists(Path.Combine(harness.StagingDirectory, upload.StagingFileName)).Should().BeTrue();
        (await dbContext.Files.CountAsync()).Should().Be(0);
        harness.ObjectStoreFactory.PutCalls.Should().Be(0);
    }

    [Fact]
    public async Task CompleteUploadAsync_preserves_pending_upload_when_object_storage_fails_then_recovers_on_retry()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var userId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("retry after an S3 failure");
        var upload = await CreateAndFillUploadAsync(harness, userId, bytes);
        harness.ObjectStoreFactory.FailAfterPersistingNextPut = true;

        await using (var failedContext = harness.CreateDbContext())
        {
            var failedService = harness.CreateService(failedContext);
            var action = () => failedService.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);
            await action.Should().ThrowAsync<IOException>().WithMessage("Simulated S3 failure after the original write.");
            (await failedContext.UploadSessions.SingleAsync()).State.Should().Be("pending");
            (await failedContext.Files.CountAsync()).Should().Be(0);
            File.Exists(Path.Combine(harness.StagingDirectory, upload.StagingFileName)).Should().BeTrue();
            harness.ObjectStoreFactory.Originals[Hash(bytes)].Should().Equal(bytes);
        }

        await using (var retryContext = harness.CreateDbContext())
        {
            var retryService = harness.CreateService(retryContext);
            var file = await retryService.CompleteUploadAsync(userId, upload.Id, new CompleteUploadReq { Sha256 = Hash(bytes) }, CancellationToken.None);
            await harness.CreateMetadataProcessor(retryContext).ProcessPendingBatchAsync(10, CancellationToken.None);
            file.Name.Should().Be("upload.txt");
            (await retryContext.UploadSessions.SingleAsync()).State.Should().Be("completed");
            (await retryContext.Files.CountAsync()).Should().Be(1);
            (await retryContext.Blobs.CountAsync()).Should().Be(1);
        }

        harness.ObjectStoreFactory.PutCalls.Should().Be(2);
        harness.ObjectStoreFactory.Originals[Hash(bytes)].Should().Equal(bytes);
        File.Exists(Path.Combine(harness.StagingDirectory, upload.StagingFileName)).Should().BeFalse();
    }

    private static async Task<UploadSession> CreateAndFillUploadAsync(MediaTestHarness harness, Guid userId, byte[] bytes)
    {
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateService(dbContext);
        var created = await service.CreateUploadAsync(userId, new CreateUploadReq
        {
            Name = "upload.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, CancellationToken.None);
        await service.AppendUploadChunkAsync(userId, created.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), CancellationToken.None);
        return await dbContext.UploadSessions.SingleAsync(upload => upload.Id == created.Id);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class AsyncOnlyWriteStream : Stream
    {
        private readonly MemoryStream destination = new();

        public int SynchronousWriteAttempts { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => destination.ToArray();

        public override void Flush()
        {
            SynchronousWriteAttempts++;
            throw new InvalidOperationException("Synchronous operations are disabled.");
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            destination.FlushAsync(cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            SynchronousWriteAttempts++;
            throw new InvalidOperationException("Synchronous writes are disabled.");
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            SynchronousWriteAttempts++;
            throw new InvalidOperationException("Synchronous writes are disabled.");
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            destination.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            destination.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }
}
