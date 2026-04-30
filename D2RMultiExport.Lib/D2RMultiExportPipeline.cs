// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib;

/// <summary>
/// Structured progress payload reported once per declared milestone phase of
/// <see cref="D2RMultiExportPipeline.RunAsync"/>. Consumers (e.g. the Avalonia
/// GUI) can use <see cref="Percent"/> to drive a real percentage progress bar
/// instead of an indeterminate animation.
/// </summary>
/// <param name="Current">1-based index of the current phase.</param>
/// <param name="Total">Total number of milestone phases for the run.</param>
/// <param name="Message">Human-readable phase description (same string also reported via the log channel).</param>
public sealed record PipelineProgress(int Current, int Total, string Message)
{
    /// <summary>Completion ratio in the range [0, 100].</summary>
    public double Percent => Total > 0 ? (double)Current / Total * 100.0 : 0.0;
}

/// <summary>
/// Main orchestrator for the doc-generation pipeline.
/// Replaces the old Importer class with async, config-driven, per-item error handling.
/// </summary>
public sealed class D2RMultiExportPipeline
{
    private readonly string _excelPath;
    private readonly string _translationsPath;
    private readonly string _exportPath;
    private readonly string _configPath;

    public PipelineResult Result { get; } = new();
    public GameData? Data { get; private set; }

    /// <summary>
    /// Number of missing translation keys that survived the filter — i.e. keys that
    /// are actually referenced inside one of the exported <c>keyed/*.json</c>
    /// files. This is the count translators should act on (and that gets written to
    /// <c>extras/missing-translations.txt</c>); it is typically much smaller than
    /// the raw <see cref="TranslationService.MissingKeys"/> total.
    /// </summary>
    public int ReferencedMissingKeyCount { get; private set; }

    /// <summary>
    /// All-13-languages translation set, populated at run start. Available to consumers
    /// after <see cref="RunAsync"/> completes and used by <c>LanguageBundleExporter</c>.
    /// </summary>
    public MultiLanguageTranslationService? MultiLangTranslations { get; private set; }

    // Pipeline options
    public bool PrettyPrintJson { get; set; } = true;
    public bool EarlyStopSentinelEnabled { get; set; } = true;
    public bool CubeRecipeUseDescription { get; set; }

    /// <summary>
    /// When <c>true</c>, exceptions thrown by an individual pipeline phase
    /// (data load, importer, exporter, extras-writer) are caught, recorded
    /// into <see cref="Result"/> as a fatal error, and the pipeline continues
    /// with the next phase. When <c>false</c> (default) any phase exception
    /// aborts the run and is rethrown to the caller.
    /// </summary>
    public bool ContinueOnException { get; set; }

    /// <summary>
    /// Optional progress sink that receives one human-readable status line per
    /// phase boundary plus a per-error/warning line. The Avalonia GUI binds
    /// this to its log panel; the console front-end leaves it null and relies
    /// on <see cref="PipelineResult.GenerateReport"/> for the final dump.
    /// </summary>
    public IProgress<string>? Progress { get; set; }

    /// <summary>
    /// Optional structured progress sink fired once per declared milestone phase
    /// (<see cref="TotalPhases"/> entries total). The Avalonia GUI binds this to a
    /// real percentage progress bar; the console front-end leaves it null. Phase
    /// boundaries are reported via <see cref="ReportPhase"/>; intermediate
    /// per-error / per-summary log lines emitted through <see cref="Report"/> do
    /// NOT advance the counter.
    /// </summary>
    public IProgress<PipelineProgress>? StructuredProgress { get; set; }

    /// <summary>Optional path to the read-only CASC base strings (fills any keys missing from mod overrides).</summary>
    public string? BaseStringsPath { get; set; }

    /// <summary>
    /// Total number of milestone phases reported via <see cref="ReportPhase"/>
    /// during a normal <see cref="RunAsync"/> invocation. Kept in sync with the
    /// list of <c>ReportPhase(...)</c> call sites; if you add or remove one, bump
    /// this constant so the progress bar still reaches 100%.
    /// </summary>
    private const int TotalPhases = 13;

    private int _phaseIndex;

    private void Report(string message) => Progress?.Report(message);

    private void ReportPhase(string message)
    {
        _phaseIndex++;
        Progress?.Report(message);
        StructuredProgress?.Report(new PipelineProgress(_phaseIndex, TotalPhases, message));
    }

    /// <summary>
    /// Executes <paramref name="action"/>; on exception records it under
    /// <paramref name="category"/> in <see cref="Result"/> and either swallows
    /// or rethrows depending on <see cref="ContinueOnException"/>.
    /// Returns <c>true</c> on success, <c>false</c> on swallowed failure.
    /// </summary>
    private async Task<bool> RunPhaseAsync(string category, string itemId, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            Result.AddError(category, itemId, $"Phase failed: {ex.Message}", ex);
            Report($"[ERROR] {category}/{itemId}: {ex.Message}");
            if (!ContinueOnException) throw;
            return false;
        }
    }

    /// <summary>
    /// Runs an importer through <see cref="RunPhaseAsync"/>, merges its
    /// <see cref="ImportResult{T}"/> into the pipeline result, assigns the
    /// imported collection back onto <see cref="GameData"/> via
    /// <paramref name="assignCollection"/>, and emits a uniform progress
    /// summary. Collapses what used to be four near-identical 6-line blocks
    /// in <see cref="RunAsync"/>.
    /// </summary>
    private async Task RunImportPhaseAsync<TItem>(
        string category,
        string fileId,
        string pluralLabel,
        string singularItemNoun,
        string summaryLabel,
        Func<Task<ErrorHandling.ImportResult<TItem>>> import,
        Action<List<TItem>> assignCollection)
    {
        ReportPhase($"Importing {pluralLabel}...");
        await RunPhaseAsync(category, fileId, async () =>
        {
            var r = await import();
            Result.Merge(r);
            assignCollection(r.Items);
            Report($"  {summaryLabel}: {r.Items.Count} {singularItemNoun}(s), {r.Errors.Count} issue(s)");
        });
    }

    public D2RMultiExportPipeline(string excelPath, string translationsPath, string exportPath, string? configPath = null)
    {
        _excelPath = Path.GetFullPath(excelPath);
        _translationsPath = Path.GetFullPath(translationsPath);
        _exportPath = Path.GetFullPath(exportPath);
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "Config");
    }

    /// <summary>
    /// Runs the full pipeline: load config → load translations → load data → import models → export.
    /// </summary>
    public async Task RunAsync()
    {
        ValidatePaths();
        _phaseIndex = 0;
        ReportPhase("Starting export pipeline...");

        // 1. Load configuration
        ReportPhase("Loading configuration...");
        ExportConfig exportConfig = null!;
        StatOverrideConfig statOverrideConfig = null!;
        await RunPhaseAsync("Config", "export-config.json", async () =>
        {
            exportConfig = await ExportConfig.LoadAsync(Path.Combine(_configPath, "export-config.json"));
            statOverrideConfig = await StatOverrideConfig.LoadAsync(Path.Combine(_configPath, "stat-overrides.json"));
        });
        // Defensive: when ContinueOnException swallowed a config-load failure we
        // still need a non-null instance so downstream code doesn't NRE.
        exportConfig ??= new ExportConfig();
        statOverrideConfig ??= new StatOverrideConfig();

        // 2a. Load translations (legacy single-language enUS service used by the
        //     descfunc resolver until it is converted to emit KeyedLine).
        ReportPhase("Loading mod translations...");
        var translations = new TranslationService();
        await RunPhaseAsync("Translations", "mod-strings", async () =>
        {
            var jsonFiles = Directory.GetFiles(_translationsPath, "*.json", SearchOption.AllDirectories);
            if (jsonFiles.Length > 0)
            {
                await translations.LoadD2RTranslationsAsync(jsonFiles);
            }
            var supplementalPath = Path.Combine(_configPath, "supplemental-translations.json");
            await translations.LoadSupplementalAsync(supplementalPath);
        });

        // Make sure the legacy descfunc resolver knows about every synthetic enUS
        // template too, otherwise probing a synthetic key (e.g. strModEnhancedDefense)
        // would record it in MissingKeys even though the keyed bundle resolves it
        // correctly via the multi-language service.
        await SyntheticStringRegistry.LoadEnUSAsync(_configPath);
        translations.MergeSeed(SyntheticStringRegistry.EnUSSeed);

        // 2b. Load the multi-language translation set.
        //     Source precedence for the final strings/{lang}.json bundle:
        //       1. Mod overrides (data/local/lng/strings/*.json)  — primary, always emitted.
        //       2. Synthetic seeds (Config/synthetic-strings*.json) — fill keys the tool
        //          itself introduces (item-type codes, stat formats, etc.).
        //       3. CASC base strings — REFERENCE-ONLY fallback. CASC ships thousands of
        //          quest dialogue / NPC / UI strings the website never looks up; rather
        //          than dumping the whole pile into every bundle we load it into a side
        //          dictionary and only promote entries that keyed/*.json actually
        //          references (see ApplyFallback below).
        ReportPhase("Loading multi-language string bundle...");
        var multiLang = new MultiLanguageTranslationService();
        await RunPhaseAsync("Translations", "multi-lang", async () =>
        {
            await multiLang.LoadFromDirectoryAsync(_translationsPath);          // mod overrides (primary)
            await multiLang.LoadFallbackFromDirectoryAsync(BaseStringsPath);    // CASC base (reference-only)

            // Synthetic seed already loaded above for the enUS-only descfunc resolver;
            // reuse the cached value here instead of re-parsing the file.
            await ClassRangeConfig.LoadAsync(_configPath);
        });
        // The combined Config/synthetic-strings.json (D2R flat row layout) carries
        // both the enUS seed and every translator-supplied per-language column.
        multiLang.MergeSynthetic(SyntheticStringRegistry.EnUSSeed, SyntheticStringRegistry.PerLanguageSeeds);
        MultiLangTranslations = multiLang;

        // 3. Create game data context
        Data = new GameData
        {
            ExportConfig = exportConfig,
            StatOverrideConfig = statOverrideConfig,
            Translations = translations,
        };

        // 4. Load all game data files
        ReportPhase("Loading game data files...");
        await RunPhaseAsync("DataLoader", "excel-files", async () =>
        {
            var dataLoader = new DataLoader(_excelPath, Data, Result);
            await dataLoader.LoadAllAsync();
        });

        // 4b. Auto-seed itemtype-code translation keys. The exported JSON emits
        //     each itemtypes.txt row as `${code}itype` (e.g. "axe" → "axeitype")
        //     so the website filter graph has a stable, collision-free
        //     translation key per item type. None of the suffixed keys exist in
        //     vanilla CASC nor mod string files, so every itemtype gets a
        //     synthetic enUS seed sourced from the ItemType column (translators
        //     can override per language). MergeSynthetic preserves any
        //     pre-existing key, so this is safe to call after the regular
        //     synthetic merge.
        //
        //     IMPORTANT — multi-language coverage. Translators have, for many
        //     itypes, historically stored translations under the *original*
        //     itemtypes.txt ItemType name (e.g. mod files keyed on "Wand",
        //     "Orb", "Voodoo Heads", "Pelt"…). The legacy `Index = originalName`
        //     mapping connected those entries directly; switching to
        //     `${code}itype` orphaned them in every non-enUS bundle. To keep the
        //     website i18n working we also alias each per-language value found
        //     under the original Name into the new suffixed key.
        var itemTypeSeed = new Dictionary<string, string>(StringComparer.Ordinal);
        var itemTypePerLangSeed = MultiLanguageTranslationService.AllLanguages
            .Where(l => l != "enUS")
            .ToDictionary(l => l, _ => (Dictionary<string, string>)new(StringComparer.Ordinal));
        foreach (var entry in Data.ItemTypes.Values)
        {
            if (string.IsNullOrEmpty(entry.Index)) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;
            // Defensive: if a translator has already provided this exact suffixed
            // key in a mod string file or CASC fallback, leave it alone.
            if (multiLang.ContainsKeyOrFallback(entry.Index)) continue;
            itemTypeSeed[entry.Index] = entry.Name;

            // Alias: pull every per-language translation that exists under the
            // original ItemType name across mod overrides + CASC fallback into
            // the new `${code}itype` key. enUS is handled above via itemTypeSeed.
            foreach (var lang in MultiLanguageTranslationService.AllLanguages)
            {
                if (lang == "enUS") continue;
                if (multiLang.TryGetForLanguage(lang, entry.Name, out var translated)
                    && !string.IsNullOrEmpty(translated))
                {
                    itemTypePerLangSeed[lang][entry.Index] = translated;
                }
            }
        }
        if (itemTypeSeed.Count > 0)
        {
            var perLangReadOnly = itemTypePerLangSeed.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyDictionary<string, string>)kvp.Value,
                StringComparer.Ordinal);
            multiLang.MergeSynthetic(itemTypeSeed, perLangReadOnly);
            // Also feed the legacy single-language service so descfunc probes
            // do not record these codes as missing translations.
            translations.MergeSeed(itemTypeSeed);
        }

        // 5. Import model collections (uniques, runewords, sets, cube recipes).
        // Each phase follows the same load → merge errors → assign collection
        // pattern; RunImportPhaseAsync collapses that boilerplate.
        await RunImportPhaseAsync("Unique",     "UniqueItems.txt", "uniques",      "item",   "Uniques",
            () => new UniqueImporter(_excelPath, Data).ImportAsync(),
            r => Data.Uniques = r);

        await RunImportPhaseAsync("Runeword",   "runes.txt",       "runewords",    "item",   "Runewords",
            () => new RunewordImporter(_excelPath, Data).ImportAsync(),
            r => Data.Runewords = r);

        await RunImportPhaseAsync("Set",        "setitems.txt",    "sets",         "item",   "Sets",
            () => new SetImporter(_excelPath, Data).ImportAsync(),
            r => Data.Sets = r);

        await RunImportPhaseAsync("CubeRecipe", "cubemain.txt",    "cube recipes", "recipe", "Cube recipes",
            () => new CubeRecipeImporter(_excelPath, Data)
                  {
                      EarlyStopSentinelEnabled = EarlyStopSentinelEnabled,
                      CubeRecipeUseDescription = CubeRecipeUseDescription
                  }.ImportAsync(),
            r => Data.CubeRecipes = r);

        // 6. Export — key-based bundle is the only output. The website consumes
        //    keyed/*.json + strings/{lang}.json × 13 and uses manifest.json
        //    for cache-busting.
        if (!Directory.Exists(_exportPath))
            Directory.CreateDirectory(_exportPath);

        ReportPhase("Writing keyed JSON bundle...");
        await RunPhaseAsync("Export", "keyed/*.json", async () =>
        {
            await KeyedJsonExporter.ExportAsync(_exportPath, Data, PrettyPrintJson);
        });

        // 6a. Compute the set of every string that actually appears as a value inside
        //     keyed/*.json. The keyed wire format embeds translation keys only in
        //     values (NameStr, KeyedLine.key, nested arg keys), so harvesting all string
        //     values yields a superset of the keys the website will ever look up. We use
        //     this set to:
        //       - promote ONLY referenced CASC entries from the fallback layer into the
        //         per-language bundles (so quest dialogue and other unrelated CASC
        //         strings are not exported);
        //       - scope the missing-translations audit to keys translators can act on.
        var referencedKeys = await CollectReferencedKeysAsync(Path.Combine(_exportPath, "keyed"));
        // Always-include synthetic keys: rendered by the website but never embedded as
        // a value inside keyed/*.json (e.g. the per-character-level suffix is
        // appended client-side after t(key,args), so it is invisible to the harvester).
        // Without this allow-list the FilterToReferenced step below would drop them
        // from every per-language bundle.
        var alwaysInclude = new HashSet<string>(StringComparer.Ordinal)
        {
            SyntheticStringRegistry.Keys.PerCharacterLevelSuffix,
            // Runeword scope-suffix synthetics — appended client-side after a
            // KeyedLine carrying Qualifier="weapon"/"shield"/"armor", so never
            // embedded as a value inside keyed/*.json.
            SyntheticStringRegistry.Keys.RuneScopeWeapon,
            SyntheticStringRegistry.Keys.RuneScopeShield,
            SyntheticStringRegistry.Keys.RuneScopeArmor,
        };
        var augmentedReferenced = new HashSet<string>(referencedKeys, StringComparer.Ordinal);
        augmentedReferenced.UnionWith(alwaysInclude);
        multiLang.ApplyFallback(augmentedReferenced);
        // 6b. Strip every primary-bundle entry that is not referenced by the exported
        //     keyed documents. Mod string files routinely carry UI / quest / NPC text
        //     the website never reads (thousands of lines); without this filter those
        //     leak into strings/{lang}.json. Website-only UI keys live in a separate
        //     file local to the website project.
        multiLang.FilterToReferenced(augmentedReferenced);

        ReportPhase("Writing per-language string bundles...");
        await RunPhaseAsync("Export", "strings/*.json", async () =>
        {
            await LanguageBundleExporter.ExportAsync(_exportPath, multiLang, PrettyPrintJson);
        });
        // 7. Write extras (import report + missing-translations audit).
        var extrasDir = Path.Combine(_exportPath, "extras");
        if (!Directory.Exists(extrasDir))
            Directory.CreateDirectory(extrasDir);

        ReportPhase("Writing extras (audit reports)...");
        await RunPhaseAsync("Extras", "import-report.txt", async () =>
        {
            await Result.WriteReportAsync(Path.Combine(extrasDir, "import-report.txt"));
        });

        ReferencedMissingKeyCount = translations.MissingKeys.Count(referencedKeys.Contains);
        await RunPhaseAsync("Extras", "missing-translations.txt", async () =>
        {
            await translations.WriteMissingKeysReportAsync(Path.Combine(extrasDir, "missing-translations.txt"), referencedKeys);
        });

        // Dumps every key whose value(s) had to be sourced from CASC base strings
        // (i.e. referenced by the export but not supplied by the mod's own
        // local\lng\strings\*.json). Useful for deciding whether to fold a small
        // residual set into Config/synthetic-strings.json instead of relying on
        // the bundled CASC fallback. The report is purely informational and does
        // not affect the keyed/strings outputs.
        await RunPhaseAsync("Extras", "casc-fallback-report.txt", async () =>
        {
            await WriteCascFallbackReportAsync(Path.Combine(extrasDir, "casc-fallback-report.txt"), multiLang);
        });

        if (Result.HasErrors)
        {
            ReportPhase($"Pipeline finished with {Result.AllErrors.Count} issue(s) — see extras/import-report.txt");
        }
        else
        {
            ReportPhase("Pipeline finished successfully.");
        }
    }

    /// <summary>
    /// Walks every JSON file under <paramref name="keyedDir"/> and returns the set of
    /// string values found anywhere in the document tree. Property names are
    /// deliberately ignored — only values are collected, since key references in the
    /// keyed wire format always live in values (top-level identifiers like
    /// <c>NameStr</c> and every <c>KeyedLine.key</c> / nested-arg <c>key</c>).
    /// One exception: a small set of property names carry pure identifiers — base
    /// item codes (<c>Code</c>, <c>NormCode</c>, <c>UberCode</c>, <c>UltraCode</c>),
    /// the <c>AutoPrefix</c> numeric token, the <c>Vanilla</c> Y/N flag, the
    /// <c>PType</c> Prefix/Suffix discriminator, class-id fields
    /// (<c>RequiredClass</c>, <c>Class</c>, <c>ClassSpecific</c>), and the
    /// camelCase <c>code</c> field on <c>propertygroups.txt</c> parent
    /// <see cref="Models.KeyedLine"/>s (raw English group names like
    /// <c>"Magnetic-Affix1"</c>) — that are emitted for the website to use as
    /// lookup IDs but are NOT translation keys and must NOT pollute the
    /// missing-translations audit. (E.g. unique items expose <c>"Code": "cjw"</c>
    /// as the base-item identifier; the actual translation key for the base item
    /// lives on <c>Equipment.NameKey</c>.)
    /// </summary>
    private static readonly HashSet<string> IdentifierOnlyProperties = new(StringComparer.Ordinal)
    {
        "Code",
        "NormCode",
        "UberCode",
        "UltraCode",
        "AutoPrefix",
        "Vanilla",
        "PType",
        "RequiredClass",
        "Class",
        "ClassSpecific",
        // KeyedLine.Code (camelCase) — raw English propertygroup name on
        // parent lines; siblings PickMode/Children are not strings/objects
        // that contain translation keys, so no extra entries are needed.
        "code",
    };

    private static async Task<IReadOnlySet<string>> CollectReferencedKeysAsync(string keyedDir)
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(keyedDir))
            return referenced;

        foreach (var path in Directory.EnumerateFiles(keyedDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            await using var stream = File.OpenRead(path);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            CollectStrings(doc.RootElement, referenced);
        }

        return referenced;
    }

    private static void CollectStrings(System.Text.Json.JsonElement element, HashSet<string> sink)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                var s = element.GetString();
                if (!string.IsNullOrEmpty(s))
                    sink.Add(s);
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectStrings(item, sink);
                break;
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    // Skip identifier-only properties so their values aren't treated
                    // as referenced translation keys (they're base item codes,
                    // class IDs, Y/N flags, etc. — never looked up against the
                    // strings tables).
                    if (IdentifierOnlyProperties.Contains(prop.Name))
                        continue;
                    CollectStrings(prop.Value, sink);
                }
                break;
        }
    }

    private void ValidatePaths()
    {
        if (!Directory.Exists(_excelPath))
            throw new DirectoryNotFoundException($"Excel directory not found: {_excelPath}");
        if (!Directory.Exists(_translationsPath))
            throw new DirectoryNotFoundException($"Translations directory not found: {_translationsPath}");
    }

    /// <summary>
    /// Writes a human-readable list of every translation key that was sourced from
    /// the CASC base strings during the most recent <c>ApplyFallback</c> call.
    /// Each line shows the key followed by the languages whose value came from CASC
    /// (omitted when all 13 languages fell through, which is the common case).
    /// Sorted alphabetically by key for stable diffs across runs.
    /// </summary>
    private static async Task WriteCascFallbackReportAsync(string exportPath, MultiLanguageTranslationService multiLang)
    {
        var promotions = multiLang.LastFallbackPromotions;
        if (promotions.Count == 0)
        {
            await File.WriteAllTextAsync(exportPath, "=== CASC-sourced Translation Keys (0) ===\n");
            return;
        }

        var allLangCount = MultiLanguageTranslationService.AllLanguages.Length;
        var sortedKeys = promotions.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        var lines = new List<string>
        {
            $"=== CASC-sourced Translation Keys ({sortedKeys.Count}) ===",
            "Keys below were referenced by the export but not supplied by the mod's",
            "local\\lng\\strings\\*.json, so their values were promoted from the bundled",
            "CASC base strings. To stop relying on CASC for any of these, add an entry",
            "to Config/synthetic-strings.json (or to the mod's own string files).",
            "Languages in brackets indicate a partial fallback; an unbracketed key fell",
            "through to CASC for all 13 languages.",
            ""
        };

        foreach (var key in sortedKeys)
        {
            var langs = promotions[key];
            if (langs.Count == allLangCount)
                lines.Add($"  {key}");
            else
                lines.Add($"  {key}  [{string.Join(", ", langs)}]");
        }

        await File.WriteAllTextAsync(exportPath, string.Join("\n", lines));
    }
}
