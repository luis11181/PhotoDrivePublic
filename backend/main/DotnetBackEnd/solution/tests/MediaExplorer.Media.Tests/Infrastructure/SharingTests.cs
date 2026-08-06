using FluentAssertions;
using System.Security.Cryptography;
using System.Text;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class SharingTests
{
    [Fact]
    public async Task Shared_search_is_indexed_authorized_and_inherits_folder_grants()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var sharedRoot = (await catalog.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Family archive" }, default)).SuccessValue;
        var sharedMatch = (await catalog.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "February memories", ParentId = sharedRoot.Id }, default)).SuccessValue;
        var privateMatch = (await catalog.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "February private" }, default)).SuccessValue;
        var recipientMatch = (await catalog.CreateFolderAsync(recipientId, new CreateFolderReq { Name = "February personal" }, default)).SuccessValue;
        var directory = new TestShareRecipientDirectory(
            new ShareRecipientProfile(ownerId, "Owner Person", "owner@example.test"),
            new ShareRecipientProfile(recipientId, "Recipient Person", "recipient@example.test"));
        var sharing = new SharingService(dbContext, new CatalogAuthorization(dbContext), directory, NullLogger<SharingService>.Instance);

        (await sharing.SetItemShareAsync(ownerId, sharedRoot.Id, recipientId, new SetItemShareReq(), default)).IsSuccess.Should().BeTrue();

        var search = await sharing.SearchSharedAsync(recipientId, "feb", "folder", null, 100, null, default);

        search.IsSuccess.Should().BeTrue();
        search.SuccessValue.Items.Should().ContainSingle(item =>
            item.Id == sharedMatch.Id
            && item.AccessRole == "viewer"
            && item.SharedByDisplayName == "Owner Person");
        search.SuccessValue.Items.Should().NotContain(item => item.Id == privateMatch.Id || item.Id == recipientMatch.Id);

        (await sharing.RevokeItemShareAsync(ownerId, sharedRoot.Id, recipientId, default)).IsSuccess.Should().BeTrue();
        (await sharing.SearchSharedAsync(recipientId, "feb", "folder", null, 100, null, default)).SuccessValue.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Folder_share_is_inherited_and_revocation_removes_reads_but_keeps_private_favorite_state()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var root = (await catalog.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Family papers" }, default)).SuccessValue;
        var child = (await catalog.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "School", ParentId = root.Id }, default)).SuccessValue;
        var directory = new TestShareRecipientDirectory(
            new ShareRecipientProfile(ownerId, "Owner Person", "owner@example.test"),
            new ShareRecipientProfile(recipientId, "Recipient Person", "recipient@example.test"));
        var sharing = new SharingService(dbContext, new CatalogAuthorization(dbContext), directory, NullLogger<SharingService>.Instance);

        var grant = await sharing.SetItemShareAsync(ownerId, root.Id, recipientId, new SetItemShareReq(), default);
        grant.IsSuccess.Should().BeTrue();

        var roots = (await sharing.BrowseSharedAsync(recipientId, null, null, 100, "all", "name_asc", default)).SuccessValue;
        roots.Items.Should().ContainSingle(item => item.Id == root.Id && item.AccessRole == "viewer" && item.SharedByDisplayName == "Owner Person");
        var children = (await sharing.BrowseSharedAsync(recipientId, root.Id, null, 100, "all", "name_asc", default)).SuccessValue;
        children.Items.Should().ContainSingle(item => item.Id == child.Id && item.AccessRole == "viewer");

        var inheritedDetails = await catalog.GetItemAsync(recipientId, child.Id, default);
        inheritedDetails.IsSuccess.Should().BeTrue();
        inheritedDetails.SuccessValue.Capabilities.CanRename.Should().BeFalse();
        (await catalog.RenameItemAsync(recipientId, child.Id, new RenameItemReq { Name = "Nope" }, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);

        var favorite = await catalog.SetFavoriteAsync(recipientId, child.Id, new SetFavoriteReq { Favorite = true }, default);
        favorite.IsSuccess.Should().BeTrue();
        var favorites = (await catalog.BrowseFavoriteFoldersAsync(recipientId, null, 100, default)).SuccessValue;
        favorites.Items.Should().ContainSingle(item => item.Id == child.Id && item.Favorite && item.AccessRole == "viewer");

        (await sharing.RevokeItemShareAsync(ownerId, root.Id, recipientId, default)).IsSuccess.Should().BeTrue();
        (await catalog.GetItemAsync(recipientId, child.Id, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
        var favoritesAfterRevocation = (await catalog.BrowseFavoriteFoldersAsync(recipientId, null, 100, default)).SuccessValue;
        favoritesAfterRevocation.Items.Should().NotContain(item => item.Id == child.Id);
        (await dbContext.FolderUserStates.AsNoTracking().AnyAsync(state => state.UserId == recipientId && state.FolderId == child.Id && state.Favorite)).Should().BeTrue();
    }

    [Fact]
    public async Task Sharing_rejects_editor_self_disabled_recipient_and_protected_roots()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var folder = (await catalog.CreateFolderAsync(ownerId, new CreateFolderReq { Name = "Shareable" }, default)).SuccessValue;
        var libraryId = await dbContext.Libraries.AsNoTracking().Where(library => library.OwnerUserId == ownerId).Select(library => library.Id).SingleAsync();
        var protectedFolder = await dbContext.Folders.AsNoTracking().SingleAsync(candidate => candidate.LibraryId == libraryId && candidate.SystemRole == LibraryBootstrapper.DocumentsFolderRole);
        var directory = new TestShareRecipientDirectory(new ShareRecipientProfile(recipientId, "Recipient", "recipient@example.test"));
        var sharing = new SharingService(dbContext, new CatalogAuthorization(dbContext), directory, NullLogger<SharingService>.Instance);

        (await sharing.SetItemShareAsync(ownerId, folder.Id, recipientId, new SetItemShareReq { Role = "editor" }, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.ValidationCode);
        (await sharing.SetItemShareAsync(ownerId, folder.Id, ownerId, new SetItemShareReq(), default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.ValidationCode);
        (await sharing.SetItemShareAsync(ownerId, folder.Id, Guid.NewGuid(), new SetItemShareReq(), default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.ValidationCode);
        (await sharing.SetItemShareAsync(ownerId, protectedFolder.Id, recipientId, new SetItemShareReq(), default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.ConflictCode);
    }

    [Fact]
    public async Task Direct_file_share_authorizes_original_delivery_but_no_owner_mutations()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes("private shared document");
        var checksum = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await using var dbContext = harness.CreateDbContext();
        var catalog = harness.CreateResultService(dbContext);
        var upload = (await catalog.CreateUploadAsync(ownerId, new CreateUploadReq
        {
            Name = "notes.txt",
            MimeType = "text/plain",
            SizeBytes = bytes.Length
        }, default)).SuccessValue;
        (await catalog.AppendUploadChunkAsync(ownerId, upload.Id, 0, bytes.Length - 1, bytes.Length, new MemoryStream(bytes), default)).IsSuccess.Should().BeTrue();
        var file = (await catalog.CompleteUploadAsync(ownerId, upload.Id, new CompleteUploadReq { Sha256 = checksum }, default)).SuccessValue.Item;
        var directory = new TestShareRecipientDirectory(new ShareRecipientProfile(recipientId, "Recipient", "recipient@example.test"));
        var sharing = new SharingService(dbContext, new CatalogAuthorization(dbContext), directory, NullLogger<SharingService>.Instance);

        (await sharing.SetItemShareAsync(ownerId, file.Id, recipientId, new SetItemShareReq(), default)).IsSuccess.Should().BeTrue();
        var delivered = await catalog.OpenContentAsync(recipientId, file.Id, null, null, default);
        delivered.IsSuccess.Should().BeTrue();
        await using (var reader = new MemoryStream())
        {
            using var content = delivered.SuccessValue;
            await content.Content.CopyToAsync(reader);
            reader.ToArray().Should().Equal(bytes);
        }
        (await catalog.TrashItemAsync(recipientId, file.Id, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);

        (await sharing.RevokeItemShareAsync(ownerId, file.Id, recipientId, default)).IsSuccess.Should().BeTrue();
        (await catalog.OpenContentAsync(recipientId, file.Id, null, null, default)).ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
    }

    private sealed class TestShareRecipientDirectory(params ShareRecipientProfile[] profiles) : IShareRecipientDirectory
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
