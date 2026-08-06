namespace CSSApps.Users.Contracts.DTOs;

public sealed record UserContactProfileDto(
    Guid UserId,
    string DisplayName,
    string Email);
