namespace MediaExplorer.Media.Domain;

public sealed class Library
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "My library";
    public long NextItemSequence { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
