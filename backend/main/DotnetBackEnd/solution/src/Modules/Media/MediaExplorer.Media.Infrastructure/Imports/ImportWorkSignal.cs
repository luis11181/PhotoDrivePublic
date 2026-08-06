using System.Threading.Channels;

namespace MediaExplorer.Media.Infrastructure;

internal sealed class ImportWorkSignal
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
        }
    }
}
