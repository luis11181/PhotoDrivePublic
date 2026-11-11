namespace MediaExplorer.Media.Domain;

/// <summary>A machine running PhotoDrive against the shared database (for example the desktop or the laptop).</summary>
public sealed class ServerHost
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
