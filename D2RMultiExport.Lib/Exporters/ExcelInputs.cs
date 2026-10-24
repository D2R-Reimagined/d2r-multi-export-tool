// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Input handling shared by the exporters that read <c>data\global\excel</c> tables directly.
///
/// A mod tree ships only the tables it overrides, so an exporter whose inputs are absent
/// reports that through its <c>warn</c> callback and writes nothing; the pipeline then marks
/// the step skipped and removes any stale copy of its output. A table that is present but
/// malformed still throws — that is a failure, not an omission.
/// </summary>
internal static class ExcelInputs
{
    /// <summary>
    /// Returns true when every table in <paramref name="files"/> resolves through
    /// <paramref name="sources"/> (the mod's copy, else the vanilla CASC copy); otherwise
    /// reports each missing table with every location searched as a skip of
    /// <paramref name="output"/> and returns false.
    /// </summary>
    public static bool RequireAll(SourceRoots sources, string output, Action<string> warn, params string[] files)
    {
        var missing = files.Select(sources.ExcelSource).Where(static source => !source.Exists).ToList();
        if (missing.Count == 0) return true;
        var hint = sources.CascDataRoot is null
            ? "Configure the CASC Data Directory so vanilla tables can fill the gap."
            : "Neither the mod nor the CASC data root provides it.";
        warn($"Skipped {output}: {string.Join(", ", missing.Select(static m => Path.GetFileName(m.RelativePath)))} not found "
            + $"(searched {string.Join(" ; ", missing.SelectMany(static m => m.Searched))}). {hint}");
        return false;
    }

    /// <summary>
    /// Builds a case-insensitive lookup keyed by a D2R code, keeping the first row for a
    /// duplicated key and reporting each duplicate instead of throwing like
    /// <c>ToDictionary</c> would. Rows with an empty key are skipped.
    /// </summary>
    public static Dictionary<string, T> IndexFirst<T>(
        IEnumerable<T> rows,
        Func<T, string?> key,
        string file,
        string column,
        Action<string> warn)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var code = key(row);
            if (string.IsNullOrEmpty(code)) continue;
            if (!result.TryAdd(code, row))
                warn($"{file}: duplicate {column} '{code}'; the first row is used and later rows are ignored.");
        }
        return result;
    }
}
