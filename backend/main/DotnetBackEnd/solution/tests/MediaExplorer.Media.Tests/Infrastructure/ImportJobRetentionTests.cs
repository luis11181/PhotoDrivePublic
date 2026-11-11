using FluentAssertions;
using MediaExplorer.Media.Domain;
using MediaExplorer.Media.Infrastructure;
using MediaExplorer.Media.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class ImportJobRetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Prune_removes_only_clean_jobs_finished_before_the_retention_window()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        var owner = Guid.NewGuid();
        var library = await new LibraryBootstrapper(db).GetOrCreateAsync(owner, default);
        var oldClean = AddJob(db, owner, library.Id, ImportJob.CompletedState, Now.AddDays(-31), items: 3);
        var recentClean = AddJob(db, owner, library.Id, ImportJob.CompletedState, Now.AddDays(-29), items: 2);
        var oldWithErrors = AddJob(db, owner, library.Id, ImportJob.CompletedWithErrorsState, Now.AddDays(-90), items: 2, failed: 1);
        var running = AddJob(db, owner, library.Id, ImportJob.RunningState, completedAt: null, items: 1);
        await db.SaveChangesAsync();

        var pruned = await harness.CreateImportService(db).PruneFinishedJobsAsync(Now, default);

        pruned.Should().Be(1);
        (await db.ImportJobs.Select(job => job.Id).ToListAsync())
            .Should().BeEquivalentTo([recentClean, oldWithErrors, running]);
        (await db.ImportItems.CountAsync(item => item.ImportJobId == oldClean)).Should().Be(0);
        (await db.ImportItems.CountAsync()).Should().Be(5);
    }

    [Fact]
    public async Task Remove_deletes_a_finished_job_with_failures_but_refuses_running_and_foreign_jobs()
    {
        await using var harness = new MediaTestHarness();
        await harness.InitializeAsync();
        await using var db = harness.CreateDbContext();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var bootstrapper = new LibraryBootstrapper(db);
        var library = await bootstrapper.GetOrCreateAsync(owner, default);
        var otherLibrary = await bootstrapper.GetOrCreateAsync(other, default);
        var failedJob = AddJob(db, owner, library.Id, ImportJob.CompletedWithErrorsState, Now.AddDays(-1), items: 2, failed: 2);
        var running = AddJob(db, owner, library.Id, ImportJob.RunningState, completedAt: null, items: 1);
        var foreign = AddJob(db, other, otherLibrary.Id, ImportJob.CompletedState, Now.AddDays(-1), items: 1);
        await db.SaveChangesAsync();
        var imports = harness.CreateImportService(db);

        (await imports.DeleteAsync(owner, failedJob, default)).IsSuccess.Should().BeTrue();
        (await imports.DeleteAsync(owner, running, default)).IsFailure.Should().BeTrue();
        (await imports.DeleteAsync(owner, foreign, default)).IsFailure.Should().BeTrue();

        (await db.ImportJobs.Select(job => job.Id).ToListAsync()).Should().BeEquivalentTo([running, foreign]);
        (await db.ImportItems.CountAsync(item => item.ImportJobId == failedJob)).Should().Be(0);
        (await db.ImportItems.CountAsync()).Should().Be(2);
    }

    private static Guid AddJob(
        MediaCatalogDbContext db,
        Guid userId,
        Guid libraryId,
        string state,
        DateTime? completedAt,
        int items,
        int failed = 0)
    {
        var job = new ImportJob
        {
            UserId = userId,
            LibraryId = libraryId,
            SourcePath = "/imports/takeout",
            State = state,
            CreatedAt = (completedAt ?? Now).AddHours(-1),
            CompletedAt = completedAt
        };
        db.ImportJobs.Add(job);
        for (var index = 0; index < items; index++)
        {
            db.ImportItems.Add(new ImportItem
            {
                ImportJobId = job.Id,
                SourceRelativePath = $"photo-{index}.jpg",
                Name = $"photo-{index}.jpg",
                State = index < failed ? ImportItem.FailedState : ImportItem.ImportedState
            });
        }
        return job.Id;
    }
}
