using System.Text;
using CSSApps.Shared.Contracts.Primitives;
using MediaExplorer.Media.Application;
using MediaExplorer.Media.Contracts;
using Microsoft.Extensions.Configuration;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>Browses and resolves only relative folders beneath the operator's import inbox.</summary>
internal sealed class ImportSourceDirectory(IConfiguration configuration)
{
    private string Root => Path.GetFullPath(configuration["MediaStorage:Imports:SourceDirectory"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    public Result<string> Resolve(string? relativePath)
    {
        var path = relativePath ?? "";
        if (path.Length > 2048 || Path.IsPathRooted(path) || path.Contains('\\')
            || path.Any(char.IsControl) || (OperatingSystem.IsWindows() && path.Contains(':')))
            return MediaErrors.Validation("Choose a folder inside the server import folder.");
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            return MediaErrors.Validation("Choose a folder inside the server import folder.");
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Root);
            var resolved = Path.GetFullPath(Path.Combine([root, .. segments]));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolved.Equals(root, comparison) && !resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return MediaErrors.Validation("Choose a folder inside the server import folder.");
            for (var current = new DirectoryInfo(resolved); current is not null; current = current.Parent)
            {
                if (!current.Exists) return MediaErrors.Validation("The server import folder is unavailable or the selected folder no longer exists.");
                if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return MediaErrors.Validation("Symbolic-link import folders are not allowed.");
                if (current.FullName.Equals(root, comparison)) return resolved;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return MediaErrors.Validation("The server import folder is unavailable. Check its configuration and permissions.");
        }
        return MediaErrors.Validation("Choose a folder inside the server import folder.");
    }

    public Result<ImportSourceFoldersRes> List(string? path, string? cursor, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        var resolved = Resolve(path);
        if (resolved.IsFailure) return resolved.ErrorValue;
        string? after = null;
        if (cursor is not null)
        {
            try { after = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)); }
            catch (FormatException) { return MediaErrors.Validation("Import folder cursor is invalid."); }
        }
        try
        {
            var relative = Path.GetRelativePath(Root, resolved.SuccessValue).Replace('\\', '/');
            if (relative == ".") relative = "";
            var folders = new List<DirectoryInfo>();
            foreach (var folder in new DirectoryInfo(resolved.SuccessValue).EnumerateDirectories("*", new EnumerationOptions
            {
                IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (after is null || string.CompareOrdinal(folder.Name, after) > 0) folders.Add(folder);
            }
            var page = folders.OrderBy(folder => folder.Name, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 100) + 1).ToList();
            var more = page.Count > limit;
            if (more) page.RemoveAt(page.Count - 1);
            return new ImportSourceFoldersRes
            {
                Path = relative,
                ParentPath = relative.Length == 0 ? null : relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "",
                Folders = page.Select(folder => new ImportSourceFolderRes
                {
                    Name = folder.Name, Path = relative.Length == 0 ? folder.Name : relative + "/" + folder.Name
                }).ToList(),
                NextCursor = more ? Convert.ToBase64String(Encoding.UTF8.GetBytes(page[^1].Name)) : null
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MediaErrors.Validation("The selected server folder cannot be read.");
        }
    }
}
