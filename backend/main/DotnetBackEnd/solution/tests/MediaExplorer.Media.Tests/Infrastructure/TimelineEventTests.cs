using FluentAssertions;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class TimelineEventTests
{
    [Fact]
    public async Task Events_are_library_scoped_validated_and_searchable_by_title_or_description()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var service = CreateService(dbContext);

        var created = (await service.CreateAsync(ownerId, new CreateTimelineEventReq
        {
            Title = "Cartagena family trip",
            StartDate = new DateOnly(2025, 7, 12),
            EndDate = new DateOnly(2025, 7, 19),
            Description = "Our first vacation on the coast"
        }, default)).SuccessValue;

        (await service.ListAsync(ownerId, "Cartagena", 20, default)).SuccessValue.Events
            .Should().ContainSingle(item => item.Id == created.Id);
        (await service.ListAsync(ownerId, "coast", 20, default)).SuccessValue.Events
            .Should().ContainSingle(item => item.Id == created.Id);
        (await service.ListAsync(otherOwnerId, "Cartagena", 20, default)).SuccessValue.Events
            .Should().BeEmpty();
        (await service.GetAsync(otherOwnerId, created.Id, default)).ErrorValue.ErrorCode
            .Should().Be(MediaErrors.NotFoundCode);

        var invalid = await service.CreateAsync(ownerId, new CreateTimelineEventReq
        {
            Title = "Backwards",
            StartDate = new DateOnly(2025, 8, 2),
            EndDate = new DateOnly(2025, 8, 1)
        }, default);
        invalid.ErrorValue.ErrorCode.Should().Be(MediaErrors.ValidationCode);
    }

    [Fact]
    public async Task Updating_and_deleting_an_event_never_changes_catalog_files()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        var ownerId = Guid.NewGuid();
        await using var dbContext = harness.CreateDbContext();
        var service = CreateService(dbContext);
        var created = (await service.CreateAsync(ownerId, new CreateTimelineEventReq
        {
            Title = "Moving day",
            StartDate = new DateOnly(2024, 3, 1)
        }, default)).SuccessValue;
        var fileCount = await dbContext.Files.CountAsync();

        var updated = (await service.UpdateAsync(ownerId, created.Id, new UpdateTimelineEventReq
        {
            Title = "Moved home",
            StartDate = new DateOnly(2024, 3, 1),
            Description = "New apartment"
        }, default)).SuccessValue;
        updated.Title.Should().Be("Moved home");
        (await service.ListAsync(ownerId, "apartment", 20, default)).SuccessValue.Events
            .Should().ContainSingle(item => item.Id == created.Id);

        (await service.DeleteAsync(ownerId, created.Id, default)).IsSuccess.Should().BeTrue();
        (await dbContext.TimelineEvents.CountAsync()).Should().Be(0);
        (await dbContext.Files.CountAsync()).Should().Be(fileCount);
    }

    private static TimelineEventService CreateService(MediaCatalogDbContext dbContext) =>
        new TimelineEventService(
            dbContext,
            new LibraryBootstrapper(dbContext),
            NullLogger<TimelineEventService>.Instance);
}
