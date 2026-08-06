namespace MediaExplorer.Media.Application;

/// <summary>Owns an application-level media stream and every resource required for its lifetime.</summary>
public sealed class MediaBinaryContent(
    Stream content,
    string contentType,
    long contentLength,
    long totalLength,
    DateTime lastModified,
    string etag,
    IDisposable lifetime) : IDisposable
{
    public Stream Content { get; } = content;
    public string ContentType { get; } = contentType;
    public long ContentLength { get; } = contentLength;
    public long TotalLength { get; } = totalLength;
    public DateTime LastModified { get; } = lastModified;
    public string Etag { get; } = etag;

    public void Dispose()
    {
        Content.Dispose();
        lifetime.Dispose();
    }
}
