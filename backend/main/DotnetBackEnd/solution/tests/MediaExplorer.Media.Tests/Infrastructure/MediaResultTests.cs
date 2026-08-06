using FluentAssertions;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Tests.TestSupport;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class MediaResultTests
{
    [Fact]
    public async Task Expected_validation_failure_is_returned_without_throwing()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);

        var result = await service.CreateFolderAsync(Guid.NewGuid(), new CreateFolderReq { Name = " " }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorValue.ErrorCode.Should().Be(MediaErrors.ValidationCode);
        result.ErrorValue.Message.Should().Contain("between 1 and 255");
    }

    [Fact]
    public async Task Missing_catalog_item_returns_a_not_found_result()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);

        var result = await service.GetItemAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorValue.ErrorCode.Should().Be(MediaErrors.NotFoundCode);
    }

    [Fact]
    public async Task Invalid_photo_filter_returns_a_validation_result()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var dbContext = harness.CreateDbContext();
        var service = harness.CreateResultService(dbContext);

        var result = await service.BrowsePhotosAsync(Guid.NewGuid(), "unknown", null, null, 100, "all", false, "captured_desc", null, null, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorValue.ErrorCode.Should().Be(MediaErrors.ValidationCode);
    }
}
