// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RReimaginedTools.JsonFileParsers;

namespace D2RMultiExport.Lib.Translation;

/// <summary>
/// Centralized translation lookup that merges:
///   1. D2R JSON translation files (via D2RReimaginedTools TranslationFileParser)
///   2. A supplemental translation file specific to this utility
/// All name resolution goes through here — no hardcoded strings needed.
/// </summary>
public sealed class TranslationService
{
    private readonly Dictionary<string, string> _translations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _missingKeys = [];

    public IReadOnlyList<string> MissingKeys => _missingKeys;

    /// <summary>
    /// Loads one or more D2R JSON translation files (the standard item-names, item-modifiers, etc.).
    /// </summary>
    public async Task LoadD2RTranslationsAsync(params string[] jsonPaths)
    {
        foreach (var path in jsonPaths)
        {
            if (!File.Exists(path))
                continue;

            var parser = new TranslationFileParser(path);
            var entries = await parser.GetAllEntriesAsync();

            foreach (var entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.Key) && !string.IsNullOrEmpty(entry.EnUS))
                {
                    _translations[entry.Key] = RemoveColorCodes(entry.EnUS);
                }
            }
        }
    }

    /// <summary>
    /// Loads a supplemental JSON file with key/value overrides specific to this utility.
    /// Format: { "key1": "value1", "key2": "value2" }
    /// These take priority over D2R translations.
    /// </summary>
    public async Task LoadSupplementalAsync(string path)
    {
        if (!File.Exists(path))
            return;

        var json = await File.ReadAllTextAsync(path);
        var overrides = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true
        });

        if (overrides == null) return;

        foreach (var kvp in overrides)
        {
            _translations[kvp.Key] = RemoveColorCodes(kvp.Value);
        }
    }

    /// <summary>
    /// Gets the translated string for a key. Returns the key itself if not found,
    /// and tracks it as a missing key for reporting.
    /// </summary>
    public string GetValue(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return "";

        if (_translations.TryGetValue(key, out var value))
            return value;

        if (!_missingKeys.Contains(key))
            _missingKeys.Add(key);

        return key;
    }

    private static string RemoveColorCodes(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // Truncate at newline (strips pickup-line suffixes like "\n~Pick Up~ÿc0").
        // The leading rune-number suffix " ÿc9(ÿc0#NNÿc9)" must be PRESERVED so
        // runes export as e.g. "Eld Rune (#2)" — useful identifying info on
        // the website. Color codes are stripped below in a single pass.
        int nlIndex = input.IndexOf('\n');
        if (nlIndex != -1)
            input = input.Substring(0, nlIndex);

        // Strip D2R color codes (\u00FFc + 1 char), font-style item-codes
        // ("[fs]"), and '*' formatting markers in a single pass.
        var result = new System.Text.StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            // Color code: ÿc + 1 char (the third char may legitimately be ')'
            // — e.g. "ÿc0#13ÿc9)" — so we must allow i+2 == length-1 too.
            if (input[i] == '\u00FF' && i + 2 < input.Length && input[i + 1] == 'c')
            {
                i += 2; // Skip ÿ, c, and the color/style indicator char
                continue;
            }
            // Font-style item codes used by some non-EN strings, e.g.
            // "[fs]Shael-Rune" — drop the bracketed marker entirely.
            if (input[i] == '[' && i + 3 < input.Length &&
                input[i + 1] == 'f' && input[i + 2] == 's' && input[i + 3] == ']')
            {
                i += 3;
                continue;
            }
            // '*' is used as a formatting marker in D2R string tables
            if (input[i] == '*')
                continue;
            result.Append(input[i]);
        }
        return result.ToString();
    }

    /// <summary>
    /// Merges a synthetic-key seed dictionary (e.g. <see cref="SyntheticStringRegistry"/>'s
    /// enUS seed) into this single-language service. The seed has lower priority than
    /// any game/mod string already loaded, so real game text always wins. This stops
    /// synthetic-only keys from being recorded as missing when descfunc resolution
    /// happens to probe them.
    /// </summary>
    public void MergeSeed(IReadOnlyDictionary<string, string> seed)
    {
        foreach (var kvp in seed)
        {
            if (string.IsNullOrEmpty(kvp.Key)) continue;
            if (_translations.ContainsKey(kvp.Key)) continue; // never override real game text
            _translations[kvp.Key] = RemoveColorCodes(kvp.Value);
        }
    }

    /// <summary>
    /// Checks if a translation key exists.
    /// </summary>
    public bool ContainsKey(string? key)
    {
        return !string.IsNullOrEmpty(key) && _translations.ContainsKey(key);
    }

    /// <summary>
    /// Tries to look up a translation without recording the key as missing on a miss.
    /// Use this for fallback chains (e.g. try Code first, then NameStr) where a miss
    /// is expected and shouldn't be reported.
    /// </summary>
    public bool TryGetValue(string? key, out string value)
    {
        if (!string.IsNullOrEmpty(key) && _translations.TryGetValue(key, out var v))
        {
            value = v;
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>
    /// Gets the number of loaded translations.
    /// </summary>
    public int Count => _translations.Count;

    /// <summary>
    /// Writes a report of all missing translation keys encountered during processing.
    /// </summary>
    /// <param name="exportPath">Destination report path.</param>
    /// <param name="referencedKeys">
    /// Optional filter — when supplied, only missing keys that also appear as string
    /// values inside the exported keyed item files are reported. Keys that are merely
    /// looked up during legacy descfunc resolution but never surface in
    /// <c>keyed/*.json</c> are skipped, since translators can't act on them.
    /// </param>
    public async Task WriteMissingKeysReportAsync(string exportPath, IReadOnlySet<string>? referencedKeys = null)
    {
        IEnumerable<string> source = _missingKeys;
        if (referencedKeys is not null)
            source = source.Where(referencedKeys.Contains);

        var filtered = source.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (filtered.Count == 0)
        {
            // Always overwrite any stale report so re-runs reflect the current state.
            await File.WriteAllTextAsync(exportPath, "=== Missing Translation Keys (0) ===\n");
            return;
        }

        var lines = new List<string>
        {
            $"=== Missing Translation Keys ({filtered.Count}) ===",
            "Add these to your supplemental translation file to resolve them.",
            ""
        };
        lines.AddRange(filtered.Select(k => $"  {k}"));

        await File.WriteAllTextAsync(exportPath, string.Join("\n", lines));
    }

}
