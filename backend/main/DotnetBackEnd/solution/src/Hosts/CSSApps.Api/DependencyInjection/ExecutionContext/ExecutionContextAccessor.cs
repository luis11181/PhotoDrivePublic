using CSSApps.Shared.Contracts.Interfaces;
using CSSApps.Shared.Utils.Common.Auth;
using CSSApps.Shared.Utils.ApiHelpers.Middlewares;

namespace CSSApps.Api.DependencyInjection.ExecutionContext;

internal sealed class ExecutionContextAccessor : IExecutionContextAccessor, IExecutionContextImpersonationAccessor
{
    // Some workflows run outside an HTTP request, for example AI work triggered by integration
    // events. In those cases we still need an ambient acting user so memory and user-bound tools
    // can resolve the same execution context shape they would get from HttpContext.
    private static readonly AsyncLocal<ScopedExecutionContext?> ScopedContext = new();
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ExecutionContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            if (ScopedContext.Value?.UserId is { } scopedUserId)
            {
                return scopedUserId;
            }

            var user = _httpContextAccessor.HttpContext?.User;
            if (user is null)
            {
                return null;
            }

            var userIdentifier = AuthUtils.GetUserIdentifier(user);
            if (Guid.TryParse(userIdentifier, out var userId))
            {
                return userId;
            }

            return null;
        }
    }

    public Guid? CorrelationId
    {
        get
        {
            if (ScopedContext.Value?.CorrelationId is { } scopedCorrelationId)
            {
                return scopedCorrelationId;
            }

            if (_httpContextAccessor.HttpContext != null && _httpContextAccessor.HttpContext.Request.Headers.Keys.Any(
                    x => x == CorrelationIdMiddleware.CorrelationHeaderKey))
            {
                // try parse header value to guid and return it if it's valid
                if (
                    Guid.TryParse(
                        _httpContextAccessor.HttpContext.Request.Headers[CorrelationIdMiddleware.CorrelationHeaderKey],
                        out var res)
                )
                    return res;
                return null;
            }

            return null;
        }
    }

    public bool IsAvailable => _httpContextAccessor.HttpContext != null;

    public IDisposable BeginImpersonationScope(Guid actingUserId, Guid? correlationId = null)
    {
        var previous = ScopedContext.Value;
        ScopedContext.Value = new ScopedExecutionContext(actingUserId, correlationId);
        return new DisposeAction(() => ScopedContext.Value = previous);
    }

    private sealed record ScopedExecutionContext(Guid UserId, Guid? CorrelationId);

    private sealed class DisposeAction(Action onDispose) : IDisposable
    {
        public void Dispose()
        {
            onDispose();
        }
    }
}
