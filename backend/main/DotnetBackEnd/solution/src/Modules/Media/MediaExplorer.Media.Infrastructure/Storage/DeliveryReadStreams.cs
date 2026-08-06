using Microsoft.Extensions.Logging;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Reads no more than the requested window and owns the underlying stream.</summary>
internal sealed class LengthLimitedReadStream(Stream source, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining == 0) return 0;
        var read = source.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        if (_remaining == 0) return 0;
        var read = source.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining == 0) return 0;
        var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Copies bytes already consumed by a complete remote read into an atomic cache population.
/// An early dispose leaves no visible or reusable partial cache entry.
/// </summary>
internal sealed class CacheThroughReadStream(
    Stream source,
    IOriginalCachePopulation population,
    ILogger logger,
    Guid itemId) : Stream
{
    private IOriginalCachePopulation? _population = population;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.CanSeek ? source.Length : throw new NotSupportedException();
    public override long Position
    {
        get => source.CanSeek ? source.Position : throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        if (read == 0)
        {
            CompletePopulationAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            WritePopulationAsync(buffer.AsMemory(offset, read), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = source.Read(buffer);
        if (read == 0)
        {
            CompletePopulationAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        else if (_population is not null)
        {
            try
            {
                _population.WriteAsync(buffer[..read].ToArray(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                DisablePopulation(exception);
            }
        }
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken);
        if (read == 0)
        {
            await CompletePopulationAsync(cancellationToken);
        }
        else
        {
            await WritePopulationAsync(buffer[..read], cancellationToken);
        }
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async ValueTask WritePopulationAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_population is null) return;
        try
        {
            await _population.WriteAsync(buffer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DisablePopulation(exception);
        }
    }

    private async Task CompletePopulationAsync(CancellationToken cancellationToken)
    {
        if (_population is null) return;
        try
        {
            await _population.CompleteAsync(cancellationToken);
            logger.LogInformation("[media.cache] cache-through completed itemId={ItemId}", itemId);
            _population = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            DisablePopulation(exception);
        }
    }

    private void DisablePopulation(Exception exception)
    {
        logger.LogWarning(
            "[media.cache] cache-through abandoned itemId={ItemId} exceptionType={ExceptionType}",
            itemId,
            exception.GetType().Name);
        try
        {
            _population?.Dispose();
        }
        catch (Exception disposeException)
        {
            logger.LogWarning(
                "[media.cache] cache-through cleanup deferred itemId={ItemId} exceptionType={ExceptionType}",
                itemId,
                disposeException.GetType().Name);
        }
        _population = null;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _population?.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "[media.cache] cache-through cleanup deferred itemId={ItemId} exceptionType={ExceptionType}",
                    itemId,
                    exception.GetType().Name);
            }
            _population = null;
            source.Dispose();
        }
        base.Dispose(disposing);
    }
}
