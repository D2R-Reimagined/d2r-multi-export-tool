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
    public static async Task ExportAsync(string exportDir, string excelPath, bool prettyPrint = true)
    {
        var prefixes = await ReadNameTableAsync(Path.Combine(excelPath, "rareprefix.txt"));
        var suffixes = await ReadNameTableAsync(Path.Combine(excelPath, "raresuffix.txt"));
        var names = suffixes.Concat(prefixes.Skip(1)).ToList();
        var keyedDir = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(keyedDir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await using var stream = File.Create(Path.Combine(keyedDir, "rare-names.json"));
        await JsonSerializer.SerializeAsync(stream, new RareNamePresentation(names), options);
    }

    private static async Task<IReadOnlyList<string?>> ReadNameTableAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Rare item names cannot be exported because '{Path.GetFileName(path)}' is missing.",
                path);
        }

        var result = new List<string?> { null };
        var lines = await File.ReadAllLinesAsync(path);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var separator = line.IndexOf('\t');
            var name = (separator < 0 ? line : line[..separator]).Trim();
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }

    private sealed record RareNamePresentation(IReadOnlyList<string?> Names);
}
