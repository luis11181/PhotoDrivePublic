namespace CSSApps.Api.Authentication.ApiKey;

/// <summary>
/// Configuration for a service API key.
/// Maps an API key to a seeded User record in the database.
/// User attributes (name, email, roles) come from the database, not config.
/// </summary>
public class ServiceApiKeyConfig
{
    /// <summary>
    /// The User ID (GUID) of the seeded service account user in the database.
    /// This user should be created via EF Core seeding in SeedDbExtensionMethod.
    /// </summary>
    public required Guid UserId { get; set; }

    /// <summary>
    /// Whether this API key is currently active.
    /// Allows disabling a key without removing it from config.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional expiration date for the API key.
    /// The User record remains valid; only this specific API key expires.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Optional description for this API key (for documentation/auditing).
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Root configuration for service API keys.
/// </summary>
public class ServiceApiKeysConfig
{
    /// <summary>
    /// Dictionary mapping API key strings to their configuration.
    /// The actual user data is fetched from the database using UserId.
    /// </summary>
    public Dictionary<string, ServiceApiKeyConfig> Keys { get; init; } = [];
}
