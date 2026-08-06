namespace CSSApps.Shared.Contracts.Interfaces;

public interface IExecutionContextAccessor
{
    Guid? UserId { get; }

    Guid? CorrelationId { get; }

    bool IsAvailable { get; }
}