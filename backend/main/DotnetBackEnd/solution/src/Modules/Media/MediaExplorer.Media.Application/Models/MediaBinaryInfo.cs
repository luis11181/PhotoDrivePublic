namespace MediaExplorer.Media.Application;

/// <summary>Authenticated immutable-original metadata that does not require opening the stored bytes.</summary>
public sealed record MediaBinaryInfo(
    string ContentType,
    long ContentLength,
    DateTime LastModified,
    string Etag);
