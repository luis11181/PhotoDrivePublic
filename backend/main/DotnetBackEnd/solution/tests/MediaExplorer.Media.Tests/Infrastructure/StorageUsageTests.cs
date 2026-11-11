using FluentAssertions;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.Extensions.Caching.Memory;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class StorageUsageTests
{
    [Fact]
    public async Task Usage_deduplicates_recursively_and_keeps_library_and_version_boundaries()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        var bootstrapper = new LibraryBootstrapper(db);
        var owner = Guid.NewGuid();
        var library = await bootstrapper.GetOrCreateAsync(owner, default);
        var other = await bootstrapper.GetOrCreateAsync(Guid.NewGuid(), default);
        var parent = new CatalogFolder { LibraryId = library.Id, Name = "parent" };
        var child = new CatalogFolder { LibraryId = library.Id, ParentFolderId = parent.Id, Name = "child" };
        db.Folders.AddRange(parent, child);
        var shared = new Blob { Sha256 = new string('a',64), ObjectKey = "test-a", SizeBytes = 100 };
        var old = new Blob { Sha256 = new string('b',64), ObjectKey = "test-b", SizeBytes = 40 };
        var trash = new Blob { Sha256 = new string('c',64), ObjectKey = "test-c", SizeBytes = 60 };
        db.Blobs.AddRange(shared, old, trash);
        CatalogFile File(Guid libraryId, Guid? folder, Blob blob, bool trashed = false) {
            var file = new CatalogFile { LibraryId=libraryId, ParentFolderId=folder, Name="sample", SizeBytes=blob.SizeBytes, TrashedAt=trashed ? DateTime.UtcNow : null };
            var version = new FileVersion { FileId=file.Id, BlobSha256=blob.Sha256, SizeBytes=blob.SizeBytes, MimeType="video/mp4", DeclaredMimeType="video/mp4", OriginalName="sample", CreatedAt=new DateTime(2026,1,2) };
            file.CurrentVersionId=version.Id; db.Files.Add(file); db.FileVersions.Add(version); return file;
        }
        var active = File(library.Id, child.Id, shared);
        File(library.Id, child.Id, shared, true);
        File(library.Id, parent.Id, trash, true);
        var outsider = File(other.Id, null, trash);
        db.FileVersions.Add(new FileVersion { FileId=active.Id, BlobSha256=old.Sha256, SizeBytes=40, MimeType="text/plain", DeclaredMimeType="text/plain", OriginalName="old", CreatedAt=new DateTime(2025,12,1) });
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StorageUsageService(db, bootstrapper, cache);
        var usage = (await service.GetAsync(owner, refresh: false, default)).SuccessValue;
        usage.PhysicalBytes.Should().Be(200);
        usage.LogicalBytes.Should().Be(300);
        usage.VideoBytes.Should().Be(160);
        usage.TrashBytes.Should().Be(60);
        usage.RetainedVersionBytes.Should().Be(40);
        usage.Folders.Single(f=>f.Id==parent.Id).Bytes.Should().Be(200);
        usage.Folders.Single(f=>f.Id==child.Id).Bytes.Should().Be(140);
        usage.Growth.Sum(g=>g.Bytes).Should().Be(200);
        (await service.GetAsync(other.OwnerUserId, refresh: false, default)).SuccessValue.PhysicalBytes.Should().Be(60);
        var files = (await service.FilesAsync(owner,"all",null,null,0,default)).SuccessValue;
        files.Files.Should().HaveCount(4).And.NotContain(f=>f.Id==outsider.Id);
        (await service.FilesAsync(owner,"folder",null,child.Id,0,default)).SuccessValue.Files.Should().HaveCount(3);
        (await service.FilesAsync(owner,"versions",null,null,0,default)).SuccessValue.Files.Should().ContainSingle(f=>!f.Current);
        (await service.FilesAsync(owner,"month","2025-12",null,0,default)).SuccessValue.Files.Should().ContainSingle();
        (await service.FilesAsync(owner,"folder",null,Guid.NewGuid(),0,default)).SuccessValue.Files.Should().BeEmpty();
        (await service.FilesAsync(owner,"month","invalid",null,0,default)).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Usage_is_cached_until_refresh_and_file_pages_stay_live()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        var bootstrapper = new LibraryBootstrapper(db);
        var owner = Guid.NewGuid();
        var library = await bootstrapper.GetOrCreateAsync(owner, default);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StorageUsageService(db, bootstrapper, cache);
        var emptyUsage = (await service.GetAsync(owner, refresh: false, default)).SuccessValue;
        emptyUsage.PhysicalBytes.Should().Be(0);
        var blob = new Blob { Sha256=new string('d',64), ObjectKey="test-d", SizeBytes=5_000_000_000 };
        db.Blobs.Add(blob);
        for(var i=0;i<51;i++) {
            var file = new CatalogFile { LibraryId=library.Id, Name=$"file-{i}", SizeBytes=blob.SizeBytes };
            var version = new FileVersion { FileId=file.Id, BlobSha256=blob.Sha256, SizeBytes=blob.SizeBytes, MimeType="text/plain", DeclaredMimeType="text/plain", OriginalName=file.Name };
            file.CurrentVersionId=version.Id; db.Files.Add(file); db.FileVersions.Add(version);
        }
        await db.SaveChangesAsync();
        var first=(await service.FilesAsync(owner,"all",null,null,0,default)).SuccessValue;
        var last=(await service.FilesAsync(owner,"all",null,null,50,default)).SuccessValue;
        first.Files.Should().HaveCount(50); first.HasMore.Should().BeTrue();
        last.Files.Should().ContainSingle(); last.HasMore.Should().BeFalse();
        first.Files.Select(f=>f.Id).Should().NotContain(last.Files[0].Id);
        var laterRequest = new StorageUsageService(db, bootstrapper, cache);
        var cached = (await laterRequest.GetAsync(owner, refresh: false, default)).SuccessValue;
        cached.PhysicalBytes.Should().Be(0);
        cached.CalculatedAt.Should().Be(emptyUsage.CalculatedAt);
        var usage = (await laterRequest.GetAsync(owner, refresh: true, default)).SuccessValue;
        usage.PhysicalBytes.Should().Be(5_000_000_000);
        usage.LogicalBytes.Should().Be(255_000_000_000);
        (await service.GetAsync(owner, refresh: false, default)).SuccessValue.Should().BeSameAs(usage);
    }
}
