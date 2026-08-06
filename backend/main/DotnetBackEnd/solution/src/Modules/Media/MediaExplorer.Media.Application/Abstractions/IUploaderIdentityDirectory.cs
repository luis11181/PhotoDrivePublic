namespace MediaExplorer.Media.Application;

/// <summary>Host-provided bridge for immutable uploader recovery hints.</summary>
public interface IUploaderIdentityDirectory
{
    Task<string> GetEmailAsync(Guid userId, CancellationToken cancellationToken);
}
