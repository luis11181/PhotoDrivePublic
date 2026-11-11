using System.Reflection;
using CSSApps.Shared.Infrastructure.Data.Turso;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MediaExplorer.Media.Tests.Infrastructure;

public sealed class TursoReplicaPullTests
{
    [Fact]
    public async Task Failed_pull_does_not_satisfy_another_queued_caller()
    {
        // Leave the replica uninitialized to fail each pull without making any network requests.
        await using var database = new TursoDatabase(Options.Create(new TursoDatabaseOptions
        {
            Url = "turso://unused.example.test",
            AuthToken = "test-only",
            ReplicaPath = "unused.replica.db"
        }), NullLogger<TursoDatabase>.Instance);
        var gate = (SemaphoreSlim)typeof(TursoDatabase)
            .GetField("_pullGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(database)!;
        await gate.WaitAsync();
        var first = database.PullAsync(CancellationToken.None);
        var second = database.PullAsync(CancellationToken.None);
        gate.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
    }
}
