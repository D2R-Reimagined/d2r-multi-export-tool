// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Exports the generated rare/crafted name lookup used by the save format.
/// The two saved IDs index one combined table: null, every raresuffix.txt row,
/// then every rareprefix.txt row. Each name is a localization key from
/// item-nameaffixes.json.
/// </summary>
public static class RareNameExporter
{
    public const string OutputPath = "keyed/rare-names.json";

    /// <summary>
    /// Writes <c>keyed/rare-names.json</c>. Mods carry only overrides, so the tables come
    /// from the mod when it ships them and from the CASC data root otherwise. Neither having
    /// them is a missing output, not a broken run: it is reported through
    /// <paramref name="warn"/> and skipped rather than thrown.
    /// </summary>
    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        bool prettyPrint,
        Action<string> warn)
    {
        var prefixes = await ReadNameTableAsync(sources.ExcelSource("rareprefix.txt"), warn);
        var suffixes = await ReadNameTableAsync(sources.ExcelSource("raresuffix.txt"), warn);
        if (prefixes is null || suffixes is null) return;

        var names = suffixes.Concat(prefixes.Skip(1)).ToList();

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await outputs.WriteJsonAsync(OutputPath, new RareNamePresentation(names), options);
    }

    private static async Task<IReadOnlyList<string?>?> ReadNameTableAsync(ResolvedSource source, Action<string> warn)
    {
        if (source.Path is not { } path)
        {
            warn(
                $"Skipped {OutputPath}: '{Path.GetFileName(source.RelativePath)}' was not found (searched "
                + $"{source.DescribeSearch()}). Configure a CASC Data Directory to export rare/crafted names.");
            return null;
        }

        // A saved ID is the engine record position (see Import.EngineRowIndex): only an
        // Expansion marker line is dropped; a blank line or nameless row still occupies its
        // slot (null), so it cannot shift the names after it.
        var result = new List<string?> { null };
        var lines = await File.ReadAllLinesAsync(path);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf('\t');
            var first = separator < 0 ? line : line[..separator];
            if (Import.EngineRowIndex.IsDroppedByEngine(first)) continue;
            var name = first.Trim();
            result.Add(name.Length > 0 ? name : null);
        }
        return result;
    }

    private sealed record RareNamePresentation(IReadOnlyList<string?> Names);
}
