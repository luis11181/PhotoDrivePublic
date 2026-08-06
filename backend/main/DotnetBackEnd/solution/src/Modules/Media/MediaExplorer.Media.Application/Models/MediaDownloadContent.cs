namespace MediaExplorer.Media.Application;

/// <summary>
/// Describes an authenticated download that writes directly to the HTTP response.
/// Single-file downloads preserve the original bytes; multi-file downloads stream a ZIP.
/// </summary>
public sealed class MediaDownloadContent(
    string fileName,
    string contentType,
    long? contentLength,
    int fileCount,
    long totalBytes,
    Func<Stream, CancellationToken, Task> write)
{
    public string FileName { get; } = fileName;
    public string ContentType { get; } = contentType;
    public long? ContentLength { get; } = contentLength;
    public int FileCount { get; } = fileCount;
    public long TotalBytes { get; } = totalBytes;

    public Task WriteToAsync(Stream destination, CancellationToken cancellationToken) =>
        write(destination, cancellationToken);
}
