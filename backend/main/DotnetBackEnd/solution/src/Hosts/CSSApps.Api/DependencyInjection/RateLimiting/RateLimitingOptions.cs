using System.ComponentModel.DataAnnotations;

namespace CSSApps.Api.DependencyInjection.RateLimiting;

/// <summary>
/// Configuration for rate limiting policies.
/// Supports different limits for authenticated users, anonymous users, and service API keys.
/// </summary>
public class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Whether rate limiting is enabled globally.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The HTTP status code to return when rate limit is exceeded.
    /// Default is 429 (Too Many Requests).
    /// </summary>
    public int RejectionStatusCode { get; set; } = 429;

    /// <summary>
    /// Rate limit configuration for authenticated users (JWT, Cookie, or API Key).
    /// Partitioned by user ID for per-user limits.
    /// </summary>
    [Required]
    public RateLimitPolicyOptions Authenticated { get; set; } = new()
    {
        PermitLimit = 10000,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    /// <summary>
    /// High-volume fallback for ordinary anonymous endpoints, partitioned by IP address.
    /// Protected application endpoints reject anonymous callers before this limiter runs.
    /// </summary>
    [Required]
    public RateLimitPolicyOptions Anonymous { get; set; } = new()
    {
        PermitLimit = 1000,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    /// <summary>
    /// Strict per-IP, per-endpoint protection for anonymous credential and token operations.
    /// Keep this independent from the high-volume general policies.
    /// </summary>
    [Required]
    public RateLimitPolicyOptions StrictAuthentication { get; set; } = new()
    {
        PermitLimit = 5,
        WindowSeconds = 60,
        QueueLimit = 0
    };

    /// <summary>
    /// Global rate limit configuration that applies to all requests regardless of user/IP.
    /// Acts as a safety net to prevent system overload.
    /// </summary>
    [Required]
    public GlobalRateLimitOptions Global { get; set; } = new();
}

/// <summary>
/// Configuration for the global (non-partitioned) rate limiter.
/// This is a single bucket shared by all requests to prevent system overload.
/// </summary>
 public class GlobalRateLimitOptions
{
    /// <summary>
    /// Whether the global rate limiter is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum number of requests permitted across all users in the time window.
    /// Should be set high enough to allow normal traffic but prevent system overload.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100000;

    /// <summary>
    /// Time window duration in seconds.
    /// </summary>
    [Range(1, 86400)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of requests that can be queued when limit is reached.
    /// </summary>
    [Range(0, 10000)]
    public int QueueLimit { get; set; } = 0;
}

/// <summary>
/// Configuration for a single rate limit policy using fixed window algorithm.
/// </summary>
public class RateLimitPolicyOptions
{
    /// <summary>
    /// Maximum number of requests permitted in the time window.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 100;

    /// <summary>
    /// Time window duration in seconds.
    /// </summary>
    [Range(1, 86400)] // 1 second to 24 hours
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of requests that can be queued when limit is reached.
    /// Set to 0 to disable queuing (immediate rejection).
    /// </summary>
    [Range(0, 1000)]
    public int QueueLimit { get; set; } = 10;

    /// <summary>
    /// Processing order for queued requests.
    /// </summary>
    public QueueProcessingOrderType QueueProcessingOrder { get; set; } = QueueProcessingOrderType.OldestFirst;
}

/// <summary>
/// Queue processing order for rate limiting.
/// </summary>
public enum QueueProcessingOrderType
{
    OldestFirst,
    NewestFirst
}
