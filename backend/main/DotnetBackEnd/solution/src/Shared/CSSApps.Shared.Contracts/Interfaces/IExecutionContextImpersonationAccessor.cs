namespace CSSApps.Shared.Contracts.Interfaces;

/// <summary>
/// Allows non-HTTP flows to provide an ambient acting user for services that normally read
/// execution context from the current request.
/// </summary>
public interface IExecutionContextImpersonationAccessor
{
    IDisposable BeginImpersonationScope(Guid actingUserId, Guid? correlationId = null);
}
