// SPDX-License-Identifier: GPL-3.0-or-later
using D2RReimaginedTools.JsonFileParsers;
using D2RReimaginedTools.Models;

namespace D2RMultiExport.Lib.Translation;

/// <summary>
/// Loads D2R translation .json files (which carry all 13 languages per row) from one or
/// more source directories and exposes a per-language dictionary suitable for writing to
/// <c>strings/{lang}.json</c> bundles.
///
/// Source precedence (later overrides earlier):
///   1. CASC base strings   (e.g. <c>C:\z_Casc\data\data\local\lng\strings</c>)
///   2. Mod strings         (e.g. <c>…\d2r-reimagined-mod\data\local\lng\strings</c>)
///   3. Synthetic strings   (registered in <see cref="SyntheticStringRegistry"/>)
///
/// Cosmetic stripping (applied to every value before it lands in a per-language dict):
///   • D2R color codes      <c>\u00FFc.</c>
///   • Rune lore prefix     leading <c>*</c> on a line
///   • Rune number suffix   text after <c> ÿc9(</c>
///   • Rune ranges          <c>(# ##)</c> patterns
///   • PICK UP suffix       <c>~PICK UP~</c> or <c>~Pick Up~</c> (newline-separated)
///   • Trailing newlines    <c>\\n</c>, <c>\n</c>, <c>\r</c>
/// </summary>
public sealed class MultiLanguageTranslationService
{
    /// <summary>The 13 language ISO codes D2R ships, in deterministic output order.</summary>
    public static readonly string[] AllLanguages =
    [
        "enUS", "zhTW", "deDE", "esES", "frFR", "itIT", "koKR",
        "plPL", "esMX", "jaJP", "ptBR", "ruRU", "zhCN"
    ];

    private readonly Dictionary<string, Dictionary<string, string>> _byLang;

    /// <summary>
    /// Side dictionary holding CASC base strings loaded via
    /// <see cref="LoadFallbackFromDirectoryAsync"/>. CASC ships thousands of
    /// keys (quest dialogue, NPC names, UI flavor) that the export never
    /// references; folding the entire database into <see cref="_byLang"/>
    /// would bloat <c>strings/{lang}.json</c> with content the website cannot
    /// use. Instead, CASC entries live here until <see cref="ApplyFallback"/>
    /// is called with the set of keys actually referenced inside the exported
    /// <c>keyed/*.json</c> files; only those entries are then promoted
    /// into <see cref="_byLang"/>.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, string>> _fallbackByLang;

    /// <summary>Keys requested via <see cref="GetEnUS"/> that were not present.</summary>
    public List<string> MissingKeys { get; } = [];

    /// <summary>
    /// Per-key list of languages whose value was promoted from CASC by the most recent
    /// <see cref="ApplyFallback"/> call — i.e. keys the export referenced but that the
    /// mod's own <c>local\lng\strings\*.json</c> did not supply for that language.
    /// Reset each time <see cref="ApplyFallback"/> runs. Empty until then.
    /// Languages within each entry are sorted in <see cref="AllLanguages"/> order.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> LastFallbackPromotions { get; private set; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    public MultiLanguageTranslationService()
    {
        _byLang = AllLanguages.ToDictionary(l => l, _ => new Dictionary<string, string>(StringComparer.Ordinal));
        _fallbackByLang = AllLanguages.ToDictionary(l => l, _ => new Dictionary<string, string>(StringComparer.Ordinal));
    }

    /// <summary>Adds (or overwrites) a translation entry across all available languages.</summary>
    private void AddEntry(TranslatableString entry)
    {
        if (string.IsNullOrEmpty(entry.Key)) return;
        SetLang(entry.Key, "enUS", entry.EnUS);
        SetLang(entry.Key, "zhTW", entry.ZhTW);
        SetLang(entry.Key, "deDE", entry.DeDE);
        SetLang(entry.Key, "esES", entry.EsES);
        SetLang(entry.Key, "frFR", entry.FrFR);
        SetLang(entry.Key, "itIT", entry.ItIT);
        SetLang(entry.Key, "koKR", entry.KoKR);
        SetLang(entry.Key, "plPL", entry.PlPL);
        SetLang(entry.Key, "esMX", entry.EsMX);
        SetLang(entry.Key, "jaJP", entry.JaJP);
        SetLang(entry.Key, "ptBR", entry.PtBR);
        SetLang(entry.Key, "ruRU", entry.RuRU);
        SetLang(entry.Key, "zhCN", entry.ZhCN);
    }

    private void SetLang(string key, string lang, string? raw)
    {
        if (raw is null) return;
        var cleaned = StripCosmetic(raw);
        _byLang[lang][key] = cleaned;
    }

    /// <summary>
    /// Loads every <c>*.json</c> under <paramref name="dir"/> using the dotnet-tools parser
    /// and folds it into the in-memory dictionaries. Missing dirs are silently skipped.
    /// </summary>
    public async Task LoadFromDirectoryAsync(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var path in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            var parser = new TranslationFileParser(path);
            List<TranslatableString> entries;
            try
            {
                entries = await parser.GetAllEntriesAsync();
            }
            catch
            {
                // Some files in the strings dir are not the translation array shape — skip.
                continue;
            }
            foreach (var e in entries) AddEntry(e);
        }
    }

    /// <summary>
    /// Loads every <c>*.json</c> under <paramref name="dir"/> into the side
    /// <see cref="_fallbackByLang"/> dictionary instead of the primary bundle. Use this
    /// for the read-only CASC base strings: only entries that turn out to be referenced
    /// by the exported documents are later promoted to the per-language output via
    /// <see cref="ApplyFallback"/>. Missing dirs are silently skipped.
    /// </summary>
    public async Task LoadFallbackFromDirectoryAsync(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var path in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            var parser = new TranslationFileParser(path);
            List<TranslatableString> entries;
            try
            {
                entries = await parser.GetAllEntriesAsync();
            }
            catch
            {
                continue;
            }
            foreach (var e in entries) AddFallbackEntry(e);
        }
    }

    private void AddFallbackEntry(TranslatableString entry)
    {
        if (string.IsNullOrEmpty(entry.Key)) return;
        SetFallbackLang(entry.Key, "enUS", entry.EnUS);
        SetFallbackLang(entry.Key, "zhTW", entry.ZhTW);
        SetFallbackLang(entry.Key, "deDE", entry.DeDE);
        SetFallbackLang(entry.Key, "esES", entry.EsES);
        SetFallbackLang(entry.Key, "frFR", entry.FrFR);
        SetFallbackLang(entry.Key, "itIT", entry.ItIT);
        SetFallbackLang(entry.Key, "koKR", entry.KoKR);
        SetFallbackLang(entry.Key, "plPL", entry.PlPL);
        SetFallbackLang(entry.Key, "esMX", entry.EsMX);
        SetFallbackLang(entry.Key, "jaJP", entry.JaJP);
        SetFallbackLang(entry.Key, "ptBR", entry.PtBR);
        SetFallbackLang(entry.Key, "ruRU", entry.RuRU);
        SetFallbackLang(entry.Key, "zhCN", entry.ZhCN);
    }

    private void SetFallbackLang(string key, string lang, string? raw)
    {
        if (raw is null) return;
        var cleaned = StripCosmetic(raw);
        _fallbackByLang[lang][key] = cleaned;
    }

    /// <summary>
    /// Promotes fallback (CASC) entries into the primary per-language bundles, but ONLY
    /// for keys present in <paramref name="referencedKeys"/> AND not already supplied by
    /// mod / synthetic sources. Returns the number of (key, language) pairs promoted.
    /// </summary>
    public int ApplyFallback(IReadOnlySet<string> referencedKeys)
    {
        var promoted = 0;
        // key → langs (in AllLanguages order); built once and exposed via
        // LastFallbackPromotions so the pipeline can dump a CASC-sourced report.
        var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var lang in AllLanguages)
        {
            if (!_fallbackByLang.TryGetValue(lang, out var fallback)) continue;
            if (!_byLang.TryGetValue(lang, out var target)) continue;
            foreach (var kvp in fallback)
            {
                if (!referencedKeys.Contains(kvp.Key)) continue;
                if (target.ContainsKey(kvp.Key)) continue;
                target[kvp.Key] = kvp.Value;
                promoted++;
                if (!byKey.TryGetValue(kvp.Key, out var langs))
                {
                    langs = [];
                    byKey[kvp.Key] = langs;
                }
                langs.Add(lang);
            }
        }
        LastFallbackPromotions = byKey.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value,
            StringComparer.Ordinal);
        return promoted;
    }

    /// <summary>
    /// Removes every entry from each per-language dictionary whose key is not present in
    /// <paramref name="referencedKeys"/>. Returns the total number of (key, language) pairs
    /// removed. Use after <see cref="ApplyFallback"/> when only keys actually consumed by
    /// the exported documents should ship in the language bundles — this prevents mod string
    /// files (which can carry UI / quest / NPC text the website never reads) from leaking
    /// into <c>strings/{lang}.json</c>.
    /// </summary>
    public int FilterToReferenced(IReadOnlySet<string> referencedKeys)
    {
        var removed = 0;
        foreach (var lang in _byLang.Keys.ToList())
        {
            var dict = _byLang[lang];
            var toRemove = new List<string>();
            foreach (var k in dict.Keys)
            {
                if (!referencedKeys.Contains(k)) toRemove.Add(k);
            }
            foreach (var k in toRemove)
            {
                dict.Remove(k);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// True when <paramref name="key"/> exists in either the primary bundle (mod /
    /// synthetic) or the CASC fallback layer. Use this when deciding whether to seed
    /// a synthetic placeholder for a given key — even if the CASC value will not end
    /// up in the bundle (because the key is unreferenced), we still don't want to
    /// shadow a real game translation that may become referenced later.
    /// </summary>
    public bool ContainsKeyOrFallback(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return _byLang["enUS"].ContainsKey(key) || _fallbackByLang["enUS"].ContainsKey(key);
    }

    /// <summary>
    /// Looks up <paramref name="sourceKey"/> in the given language's primary bundle, then
    /// the CASC fallback layer. Returns true when found. Used by pipeline aliasing logic
    /// (e.g. emitting <c>{code}itype</c> as the canonical translation key while reusing
    /// the per-language values that translators previously stored under the original
    /// itemtypes.txt <c>ItemType</c> name).
    /// </summary>
    public bool TryGetForLanguage(string lang, string sourceKey, out string value)
    {
        value = "";
        if (string.IsNullOrEmpty(sourceKey)) return false;
        if (_byLang.TryGetValue(lang, out var primary) && primary.TryGetValue(sourceKey, out var p))
        {
            value = p;
            return true;
        }
        if (_fallbackByLang.TryGetValue(lang, out var fallback) && fallback.TryGetValue(sourceKey, out var f))
        {
            value = f;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Merges synthetic-string seeds into the language bundles. Any key already present
    /// from CASC/mod sources is NOT overwritten unless <paramref name="overrideExisting"/>
    /// is true (caller's choice — typically false so genuine game text wins).
    /// </summary>
    public void MergeSynthetic(IReadOnlyDictionary<string, string> enUSSeed,
                               IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> perLangSeeds,
                               bool overrideExisting = false)
    {
        // enUS first. Synthetic values are placeholders, so they must lose to real game
        // text — both anything already in the primary bundle (mod overrides) AND
        // anything sitting in the CASC fallback layer (which may still be promoted into
        // the bundle later by ApplyFallback when referenced by an exported document).
        foreach (var kvp in enUSSeed)
        {
            if (kvp.Key.StartsWith('_')) continue; // skip _comment-style metadata
            if (overrideExisting
                || (!_byLang["enUS"].ContainsKey(kvp.Key)
                    && !_fallbackByLang["enUS"].ContainsKey(kvp.Key)))
            {
                _byLang["enUS"][kvp.Key] = kvp.Value;
            }
        }
        // Other langs: only fill if a translator has supplied a value
        foreach (var (lang, dict) in perLangSeeds)
        {
            if (!_byLang.TryGetValue(lang, out var target)) continue;
            _fallbackByLang.TryGetValue(lang, out var fallback);
            foreach (var kvp in dict)
            {
                if (kvp.Key.StartsWith('_')) continue;
                if (overrideExisting
                    || (!target.ContainsKey(kvp.Key)
                        && (fallback is null || !fallback.ContainsKey(kvp.Key))))
                {
                    target[kvp.Key] = kvp.Value;
                }
            }
        }
    }

    /// <summary>Read-only access to a per-language dictionary.</summary>
    public IReadOnlyDictionary<string, string> ForLanguage(string lang)
        => _byLang.TryGetValue(lang, out var d) ? d : new Dictionary<string, string>();

    /// <summary>
    /// Convenience for legacy code paths that need an enUS string. If the key is missing
    /// it is recorded in <see cref="MissingKeys"/> and the key itself is returned.
    /// </summary>
    public string GetEnUS(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (_byLang["enUS"].TryGetValue(key, out var v)) return v;
        // Fall back to the CASC layer so importers that look up a real game string
        // (e.g. a monster or skill name) still see the correct enUS value even though
        // the key has not yet been promoted into the primary bundle.
        if (_fallbackByLang["enUS"].TryGetValue(key, out var fb)) return fb;
        if (!MissingKeys.Contains(key)) MissingKeys.Add(key);
        return key;
    }

    public bool ContainsKey(string? key)
        => !string.IsNullOrEmpty(key) && _byLang["enUS"].ContainsKey(key);

    /// <summary>Total enUS key count — useful for logging.</summary>
    public int Count => _byLang["enUS"].Count;

    // ─────────────────────────────────────────────────────────────────────────────
    // Cosmetic stripping
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strip cosmetic markers the website should not see. Keep <c>%d %s %+d %D %S %0..%9 %%</c>
    /// substitution placeholders intact — those are needed for runtime arg substitution.
    /// </summary>
    public static string StripCosmetic(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        // PRESERVE the " ÿc9(ÿc0#NNÿc9)" rune-number suffix — it carries useful
        // identifying info (e.g. "Eld Rune (#2)") and the website wants it on
        // the rendered string. Color codes are stripped further down so the
        // suffix becomes the bare "(#2)" form.

        // Truncate at the first newline — this dumps "~PICK UP~" and other multi-line lore.
        int nlIdx = input.IndexOfAny(['\n', '\r']);
        if (nlIdx >= 0) input = input[..nlIdx];

        // Drop the literal "\\n" sequence the game uses for soft line breaks.
        input = input.Replace("\\n", " ");

        // Strip "(# ##)" range placeholders the game uses on rune descriptions.
        // Pattern variants: "(#)", "(#-#)", "(# ##)", "(##)"
        input = System.Text.RegularExpressions.Regex.Replace(input, @"\(#+(?:[ \-]#+)*\)", "").TrimEnd();

        // Strip leading "* " or "*" lore-line markers used by charms/jewels/runes.
        if (input.StartsWith("* ")) input = input[2..];
        else if (input.StartsWith('*')) input = input[1..];

        // Strip "~PICK UP~" / "~Pick Up~" patterns if any survived.
        input = System.Text.RegularExpressions.Regex.Replace(input, @"~[Pp][Ii][Cc][Kk] [Uu][Pp]~", "");

        // Strip D2R inline color codes (\u00FF + 'c' + 1-char selector).
        if (input.IndexOf('\u00FF') >= 0)
        {
            var sb = new System.Text.StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '\u00FF' && i + 2 < input.Length && input[i + 1] == 'c')
                {
                    i += 2; continue;
                }
                sb.Append(input[i]);
            }
            input = sb.ToString();
        }

        // Strip embedded standalone '*' chars (NOT after we already handled the leading one).
        input = input.Replace("*", "");

        return input.Trim();
    }
}
