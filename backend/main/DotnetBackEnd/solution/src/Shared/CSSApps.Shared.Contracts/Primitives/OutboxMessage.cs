namespace CSSApps.Shared.Contracts.Primitives;

public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string Type { get; init; }
    public required string Content { get; init; }
    public DateTime OccuredOnUtc { get; init; }
    public DateTime? ProcessedOnUtc { get; init; }
    public string? Error { get; init; }
}