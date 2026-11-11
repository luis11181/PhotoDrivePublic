using System.Data;
using System.Globalization;
using System.Text;

namespace MediaExplorer.Media.Infrastructure;

/// <summary>
/// Accent- and case-insensitive search text. Stored rows (<see cref="CatalogSearchIndex"/>) and query terms are
/// folded by the same C# function: Unicode decomposition with combining marks removed, then invariant lowercase
/// (José → jose, ÁRBOL → arbol, Ñandú → nandu).
/// </summary>
internal static class SearchText
{
    /// <summary>Bump when folding rules change so startup rebuilds the derived search tables.</summary>
    public const int Version = 2;

    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Normalized search terms, or <c>null</c> when the query has fewer than two letters or digits in total.
    /// Every term must match (AND); each term matches anywhere inside the searched text.
    /// </summary>
    public static IReadOnlyList<string>? Terms(string? query)
    {
        var terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Any(char.IsLetterOrDigit))
            .Select(Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return terms.Sum(term => term.Count(char.IsLetterOrDigit)) < 2 ? null : terms;
    }

    /// <summary>
    /// Builds <c>(instr(col, $p0) &gt; 0 OR ...) AND (...)</c> requiring every term as a plain substring of at least
    /// one column (no wildcards, so nothing to escape), and binds the term parameters on <paramref name="command"/>.
    /// </summary>
    public static string MatchAll(IDbCommand command, IReadOnlyList<string> terms, string parameterPrefix, params string[] columns)
    {
        var clauses = new List<string>(terms.Count);
        for (var index = 0; index < terms.Count; index++)
        {
            var parameter = $"${parameterPrefix}{index}";
            var value = command.CreateParameter();
            value.ParameterName = parameter;
            value.Value = terms[index];
            command.Parameters.Add(value);
            clauses.Add("(" + string.Join(" OR ", columns.Select(column => $"instr({column}, {parameter}) > 0")) + ")");
        }
        return string.Join(" AND ", clauses);
    }
}
