using System.Threading.Channels;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// Coalesces in-process wake-ups. SQLite metadata rows remain the durable work queue.
/// </summary>
internal sealed class MediaMetadataWorkSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Signal() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan maximumDelay, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maximumDelay);
        try
        {
            await _channel.Reader.ReadAsync(timeout.Token);
            while (_channel.Reader.TryRead(out _)) { }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Periodic polling recovers work whose in-memory signal was lost during a restart.
        }
    }
}
