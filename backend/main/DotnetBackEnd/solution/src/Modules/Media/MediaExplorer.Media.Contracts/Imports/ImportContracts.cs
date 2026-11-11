namespace MediaExplorer.Media.Contracts;

public sealed class CreateGoogleTakeoutImportReq
{
    /// <summary>Folder relative to the configured server import inbox; empty selects the inbox.</summary>
    public string SourcePath { get; set; } = null!;
    public Guid DestinationFolderId { get; set; }
    public string NameConflictPolicy { get; set; } = "skip";
    public bool SkipIdenticalFiles { get; set; } = true;
}

public sealed class ImportSourceFolderRes
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}

public sealed class ImportSourceFoldersRes
{
    public string Path { get; init; } = "";
    public string? ParentPath { get; init; }
    public IReadOnlyList<ImportSourceFolderRes> Folders { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class ImportOptionsRes
{
    public IReadOnlyList<ImportDestinationRes> Destinations { get; init; } = [];
}

public sealed class ImportDestinationRes
{
    public Guid Id { get; init; }
    public string Name { get; init; } = null!;
    public string Path { get; init; } = null!;
    public string? SystemRole { get; init; }
}

public sealed class ImportJobRes
{
    public Guid Id { get; init; }
    public Guid? DestinationFolderId { get; init; }
    public string DestinationPath { get; init; } = null!;
    public string SourceKind { get; init; } = null!;
    public string NameConflictPolicy { get; init; } = null!;
    public bool SkipIdenticalFiles { get; init; }
    public string State { get; init; } = null!;
    public int DiscoveredCount { get; init; }
    public int PendingCount { get; init; }
    public int ProcessingCount { get; init; }
    public int ImportedCount { get; init; }
    public int DuplicateCount { get; init; }
    public int NameConflictCount { get; init; }
    public int FailedCount { get; init; }
    public int WarningCount { get; init; }
    public int OrphanSidecarCount { get; init; }
    public int IgnoredJsonCount { get; init; }
    public IReadOnlyDictionary<string, int> Outcomes { get; init; } = new Dictionary<string, int>();
    public string? Error { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public sealed class ImportItemResultsRes
{
    public IReadOnlyList<ImportItemResultRes> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class ImportItemResultRes
{
    public Guid Id { get; init; }
    public Guid? CatalogFileId { get; init; }
    public string Name { get; init; } = null!;
    public string RelativePath { get; init; } = null!;
    public string State { get; init; } = null!;
    public string? OutcomeCode { get; init; }
    public IReadOnlyList<string> WarningCodes { get; init; } = [];
    public string? Error { get; init; }
}
