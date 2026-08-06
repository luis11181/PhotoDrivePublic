using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Domain;

namespace MediaExplorer.Media.Application;

public static class MediaErrors
{
    public const string ValidationCode = MediaDomainErrorCodes.Validation;
    public const string NotFoundCode = "MEDIA_NOT_FOUND";
    public const string ConflictCode = MediaDomainErrorCodes.Conflict;

    public static ResultError Validation(string message) => new(message, ValidationCode);
    public static ResultError NotFound(string message) => new(message, NotFoundCode);
    public static ResultError Conflict(string message) => new(message, ConflictCode);
}
