namespace MediaExplorer.Media.Infrastructure;

internal static class DbIdExtensions
{
    /// <summary>
    /// The TEXT form EF Core's SQLite provider stores GUIDs in (uppercase). Bind raw-SQL GUID parameters in this
    /// form: Turso compares indexed columns with their binary collation even when a query says COLLATE NOCASE, so a
    /// lowercase value would silently match nothing.
    /// </summary>
    public static string ToDbText(this Guid id) => id.ToString("D").ToUpperInvariant();
}
