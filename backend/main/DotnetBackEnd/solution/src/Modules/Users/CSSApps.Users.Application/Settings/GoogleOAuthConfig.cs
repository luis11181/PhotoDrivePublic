using System.ComponentModel.DataAnnotations;

namespace CSSApps.Users.Application.Settings;

public class GoogleOAuthConfig
{
    public const string SectionName = "OAuth:Google";

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1056:Uri properties should not be strings", Justification = "Configuration property")]
    public string UserInfoUrl { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) &&
        !ClientId.StartsWith("replace-", StringComparison.OrdinalIgnoreCase);
}
