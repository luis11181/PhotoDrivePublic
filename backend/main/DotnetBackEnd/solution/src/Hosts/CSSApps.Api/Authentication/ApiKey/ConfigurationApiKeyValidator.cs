using CSSApps.Shared.Contracts.Primitives;
using CSSApps.Users.Application.Interfaces;
using CSSApps.Users.Domain.Entities.UserAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CSSApps.Api.Authentication.ApiKey;

/// <summary>
/// Validates service API keys against configuration and loads user from database.
/// The API key maps to a UserId, and user attributes come from the seeded User record.
/// </summary>
public class ConfigurationServiceApiKeyValidator
{
    private readonly ILogger<ConfigurationServiceApiKeyValidator> _logger;
    private readonly IOptions<ServiceApiKeysConfig> _options;
    private readonly IUsersDbContext _dbContext;

    public ConfigurationServiceApiKeyValidator(
        IOptions<ServiceApiKeysConfig> options,
        ILogger<ConfigurationServiceApiKeyValidator> logger,
        IUsersDbContext dbContext)
    {
        _options = options;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<Result<User>> ValidateApiKeyAsync(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Empty service API key provided");
            return ServiceApiKeyErrors.ApiKeyRequired;
        }

        var apiKeys = _options.Value.Keys;

        if (apiKeys.Count == 0)
        {
            _logger.LogWarning("No service API keys configured in application settings");
            return ServiceApiKeyErrors.NotConfigured;
        }

        if (!apiKeys.TryGetValue(apiKey, out var keyConfig))
        {
            // Don't log the actual key for security, use a prefix
            _logger.LogWarning("Invalid service API key attempted (prefix: {ApiKeyPrefix}...)",
                apiKey.Length >= 8 ? apiKey[..8] : "short");
            return ServiceApiKeyErrors.InvalidApiKey;
        }

        if (!keyConfig.IsActive)
        {
            _logger.LogWarning("Inactive service API key attempted for UserId {UserId}", keyConfig.UserId);
            return ServiceApiKeyErrors.Inactive;
        }

        // Check expiration if configured
        if (keyConfig.ExpiresAt.HasValue && keyConfig.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("Expired service API key attempted for UserId {UserId}", keyConfig.UserId);
            return ServiceApiKeyErrors.Expired;
        }

        // Load user from database
        var user = await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == keyConfig.UserId);

        if (user is null)
        {
            _logger.LogError("Service API key configured for non-existent UserId {UserId}. " +
                             "Ensure the service account user is seeded in the database.", keyConfig.UserId);
            return ServiceApiKeyErrors.UserNotFound;
        }

        _logger.LogDebug("Service API key validated successfully for user {UserId} ({Email})",
            user.Id, user.Email);

        return user;
    }
}

/// <summary>
/// Common errors for service API key validation.
/// </summary>
public static class ServiceApiKeyErrors
{
    public static readonly ResultError ApiKeyRequired = new("API key is required", "API_KEY_REQUIRED");
    public static readonly ResultError NotConfigured = new("Service API key authentication not configured", "API_KEY_NOT_CONFIGURED");
    public static readonly ResultError InvalidApiKey = new("Invalid API key", "INVALID_API_KEY");
    public static readonly ResultError Inactive = new("API key is inactive", "API_KEY_INACTIVE");
    public static readonly ResultError Expired = new("API key has expired", "API_KEY_EXPIRED");
    public static readonly ResultError UserNotFound = new("Service account user not found in database", "SERVICE_USER_NOT_FOUND");
}
