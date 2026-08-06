using CSSApps.Shared.Contracts.Primitives;

namespace MediaExplorer.Media.Domain;

public static class MediaDomainErrorCodes
{
    public const string Validation = "MEDIA_VALIDATION";
    public const string Conflict = "MEDIA_CONFLICT";
}

/// <summary>Persistence-neutral invariants shared by file and folder catalog operations.</summary>
public static class CatalogItemPolicy
{
    public static Result<string> NormalizeName(string? name, string itemType)
    {
        var normalized = name?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 255
            ? new ResultError($"{itemType} names must contain between 1 and 255 characters.", MediaDomainErrorCodes.Validation)
            : normalized;
    }

    public static Result EnsureActive(DateTime? trashedAt) => trashedAt.HasValue
        ? new ResultError("Restore this item before changing it.", MediaDomainErrorCodes.Conflict)
        : Result.Success();

    public static Result EnsureMutable(CatalogFolder folder, string operation) => folder.SystemRole is not null
        ? new ResultError($"The protected {folder.Name} folder cannot be {operation}.", MediaDomainErrorCodes.Conflict)
        : Result.Success();

    public static void MarkTrashed(CatalogFolder folder, Guid rootId, DateTime trashedAt)
    {
        folder.TrashedAt = trashedAt;
        folder.TrashRootId = rootId;
        folder.ModifiedAt = trashedAt;
    }

    public static void MarkTrashed(CatalogFile file, Guid rootId, DateTime trashedAt)
    {
        file.TrashedAt = trashedAt;
        file.TrashRootId = rootId;
        file.ModifiedAt = trashedAt;
    }

    public static void Restore(CatalogFolder folder, DateTime restoredAt)
    {
        folder.TrashedAt = null;
        folder.TrashRootId = null;
        folder.ModifiedAt = restoredAt;
    }

    public static void Restore(CatalogFile file, DateTime restoredAt)
    {
        file.TrashedAt = null;
        file.TrashRootId = null;
        file.ModifiedAt = restoredAt;
    }
}
