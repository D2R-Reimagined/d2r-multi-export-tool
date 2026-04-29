// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Encodings.Web;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Writes one flat <c>strings/{lang}.json</c> file per supported language. The website
/// fetches exactly one of these for the user's active language and uses it as the
/// lookup table for every <c>KeyedLine.Key</c> emitted in the data files.
///
/// Output format (per language file):
/// <code>
/// { "ModStr3a": "+%d to Amazon Skill Levels", "strCharmWeight": "Charm Weight: %d", ... }
/// </code>
/// </summary>
public static class LanguageBundleExporter
{
    public static async Task<IReadOnlyList<string>> ExportAsync(
        string exportRootPath,
        MultiLanguageTranslationService translations,
        bool prettyPrint = true)
    {
        var stringsDir = Path.Combine(exportRootPath, "strings");
        Directory.CreateDirectory(stringsDir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            // Keep accented characters / non-ASCII text readable rather than \uXXXX-escaped.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var written = new List<string>();
        foreach (var lang in MultiLanguageTranslationService.AllLanguages)
        {
            var dict = translations.ForLanguage(lang);

            // Deterministic key order — easier diffs and faster gzip.
            var ordered = dict.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                              .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

            var path = Path.Combine(stringsDir, $"{lang}.json");
            await using var fs = File.Create(path);
            await JsonSerializer.SerializeAsync(fs, ordered, options);
            written.Add(path);
        }

        return written;
    }
}
