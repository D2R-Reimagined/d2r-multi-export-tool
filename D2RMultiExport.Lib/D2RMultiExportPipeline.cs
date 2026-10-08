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
    private readonly string _exportPath;
    private readonly string _configPath;

    /// <summary>
    /// The resolved Mod Data / CASC Data roots. Every importer and exporter reads through it,
    /// so all of them agree on where an input lives.
    /// </summary>
    public SourceRoots Sources { get; }

    /// <summary>Per-identifier sprite/icon diagnostics of the last run (<c>extras/asset-report.txt</c>).</summary>
    public AssetDiagnostics AssetDiagnostics { get; private set; } = new();

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
    /// Runs one export step in isolation. Its warnings go to <see cref="Result"/> under
    /// <c>Export/&lt;producer&gt;</c>; an exception is recorded as an error but never escapes,
    /// so later exporters and the extras reports still run. The outcome is recorded for
    /// <c>manifest.json</c>, which lists only files written in <em>this</em> run.
    /// <para>
    /// Deletion is limited to what the step itself attempted: on failure, every path the
    /// step tried to write during this run is invalidated (its set is incomplete, and an
    /// attempted path may still hold an earlier run's copy). A skipped step, or a declared
    /// output the step never attempted, is left untouched on disk and reported as
    /// <c>NotWritten</c> instead of being vouched for. No directory is ever scanned.
    /// </para>
    /// </summary>
    private async Task RunExportStepAsync(
        ExportOutputs outputs,
        string producer,
        IReadOnlyList<string> declaredOutputs,
        Func<Action<string>, Task> export)
    {
        outputs.Producer = producer;
        void Warn(string message)
        {
            Result.AddWarning("Export", producer, message);
            Report($"[WARN] {producer}: {message}");
        }

        try
        {
            await export(Warn);
        }
        catch (Exception ex)
        {
            Result.AddError("Export", producer, $"Export step failed: {ex.Message}", ex);
            Report($"[ERROR] Export/{producer}: {ex.Message}");
            var removed = InvalidateFailedStep(outputs, producer, static _ => true);
            _exportSteps.Add(new ExportStepResult(producer, ExportStepStatus.Failed, [],
                [.. declaredOutputs.Where(path => !removed.Contains(path, StringComparer.OrdinalIgnoreCase))],
                removed, ex.Message));
            _failedExportSteps.Add(producer);
            return;
        }
        finally
        {
            ReportOmittedPropertyLines(Warn);
            outputs.Producer = "";
        }

        var written = outputs.Files.Where(f => f.Producer == producer).Select(static f => f.Path).ToList();
        var notWritten = declaredOutputs.Where(path => !outputs.WasWritten(path)).ToList();
        if (written.Count == 0)
        {
            _exportSteps.Add(new ExportStepResult(producer, ExportStepStatus.Skipped, [], notWritten, [],
                "No output: a required input was absent (see extras/import-report.txt). Existing files were left untouched."));
            return;
        }
        foreach (var path in notWritten)
            Report($"  {path} was not produced by this run; any existing copy was left untouched.");
        _exportSteps.Add(new ExportStepResult(producer, ExportStepStatus.Written, written, notWritten, [], null));
    }

    /// <summary>
    /// Deletes the paths <paramref name="producer"/> attempted to write during this run
    /// (and only those, narrowed by <paramref name="filter"/>). A deletion failure is
    /// recorded rather than thrown, so the remaining steps and reports still run.
    /// </summary>
    private List<string> InvalidateFailedStep(ExportOutputs outputs, string producer, Func<string, bool> filter)
    {
        try
        {
            var removed = outputs.InvalidateAttempted(producer, filter).ToList();
            foreach (var path in removed)
                Report($"  Removed {path}: {producer} failed after attempting to write it.");
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Result.AddError("Export", producer, $"Could not remove the outputs of the failed step: {ex.Message}", ex);
            Report($"[ERROR] Export/{producer}: could not remove its outputs: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Writes one diagnostic report as its own manifest step. A failure is recorded and only
    /// that report's attempted path is removed; it never stops the remaining reports or the
    /// manifest, and the pipeline still fails at the end.
    /// </summary>
    private async Task RunReportStepAsync(ExportOutputs outputs, string path, Func<Task> write)
    {
        var producer = outputs.Producer;
        outputs.Producer = path;
        try
        {
            await write();
            _exportSteps.Add(new ExportStepResult(path, ExportStepStatus.Written, [path], [], [], null));
        }
        catch (Exception ex)
        {
            Result.AddError("Extras", path, $"Report could not be written: {ex.Message}", ex);
            Report($"[ERROR] Extras/{path}: {ex.Message}");
            var removed = InvalidateFailedStep(outputs, path,
                attempted => string.Equals(attempted, path, StringComparison.OrdinalIgnoreCase));
            _exportSteps.Add(new ExportStepResult(path, ExportStepStatus.Failed, [], removed.Count > 0 ? [] : [path], removed, ex.Message));
            _failedExportSteps.Add(path);
        }
        finally
        {
            outputs.Producer = producer;
        }
    }

    private readonly List<ExportStepResult> _exportSteps = [];
    private readonly List<string> _failedExportSteps = [];

    /// <summary>
    /// Reports property lines omitted for an unresolved template placeholder that no
    /// importer row claimed (see <see cref="PropertyMapper.TakeOmittedLines"/>).
    /// </summary>
    private void ReportOmittedPropertyLines(Action<string> warn)
    {
        if (Data is null) return;
        foreach (var omission in PropertyMapper.TakeOmittedLines(Data))
            warn(omission);
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
            ReportOmittedPropertyLines(message => Result.AddWarning(category, fileId, message));
            assignCollection(r.Items);
            Report($"  {summaryLabel}: {r.Items.Count} {singularItemNoun}(s), {r.Errors.Count} issue(s)");
        });
    }

    /// <summary>
    /// Creates a pipeline over the two source roots. <paramref name="modDataPath"/> is the mod's
    /// <c>data</c> directory (or the folder containing it); <paramref name="cascDataPath"/> is the
    /// extracted vanilla <c>data</c> directory (<c>data/</c> or CascView's <c>data/data/</c>
    /// nesting are both recognised), or <c>null</c> to run without vanilla fallback. Throws
    /// <see cref="SourceLayoutException"/> with an actionable message for an unsupported layout.
    /// </summary>
    public D2RMultiExportPipeline(string modDataPath, string? cascDataPath, string exportPath, string? configPath = null)
    {
        _exportPath = Path.GetFullPath(exportPath);
        _configPath = Path.GetFullPath(configPath ?? Path.Combine(AppContext.BaseDirectory, "Config"));
        Sources = SourceRoots.Create(modDataPath, cascDataPath, Path.Combine(_configPath, "CASC_DATA"));
    }

    /// <summary>
    /// Runs the full pipeline: load config → load translations → load data → import models → export.
    /// </summary>
    public async Task RunAsync()
    {
        ValidatePaths();
        _phaseIndex = 0;
        AssetDiagnostics = new AssetDiagnostics();
        ReportPhase("Starting export pipeline...");
        foreach (var line in Sources.Describe()) Report($"  {line}");
        foreach (var note in SourceRoots.ValidateModRoot(Sources.ModDataRoot).Notes) Report($"  {note}");
        if (Sources.CascDataRoot is { } cascRoot)
        {
            foreach (var note in SourceRoots.ValidateCascRoot(cascRoot).Notes) Report($"  {note}");
        }
        else
        {
            Result.AddWarning("Sources", "casc", "No CASC Data Directory configured: files the mod does not ship (vanilla sprites, "
                + "base strings, vanilla tables) are unavailable and the affected outputs are reduced or skipped.");
        }

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
            var jsonFiles = Directory.GetFiles(Sources.ModStringsDirectory, "*.json", SearchOption.AllDirectories);
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
            var cascStrings = Sources.CascStringsDirectory;
            Sources.TrackDirectory(SourceRoots.StringsDirectory + " (mod)", Sources.ModStringsDirectory, SourceOrigin.Mod);
            Sources.TrackDirectory(SourceRoots.StringsDirectory + " (casc)", cascStrings, SourceOrigin.Casc);
            if (cascStrings is null)
                Result.AddWarning("Translations", "casc-strings",
                    "No CASC local/lng/strings directory: keys the mod does not override cannot fall back to vanilla text.");
            await multiLang.LoadFromDirectoryAsync(Sources.ModStringsDirectory);   // mod overrides (primary)
            await multiLang.LoadFallbackFromDirectoryAsync(cascStrings);           // CASC base (reference-only)

            // Synthetic seed already loaded above for the enUS-only descfunc resolver;
            // reuse the cached value here instead of re-parsing the file.
            await ClassRangeConfig.LoadAsync(_configPath);
            await SkillDescFunctionConfig.LoadAsync(_configPath);
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
            TemplateLookup = key => multiLang.TryGetForLanguage("enUS", key, out var template) ? template : null,
        };

        // 4. Load all game data files
        ReportPhase("Loading game data files...");
        await RunPhaseAsync("DataLoader", "excel-files", async () =>
        {
            var dataLoader = new DataLoader(Sources, Data, Result);
            await dataLoader.LoadAllAsync();
        });
        ReportOmittedPropertyLines(message => Result.AddWarning("DataLoader", "excel-files", message));

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
            () => new UniqueImporter(Sources, Data).ImportAsync(),
            r => Data.Uniques = r);

        await RunImportPhaseAsync("Runeword",   "runes.txt",       "runewords",    "item",   "Runewords",
            () => new RunewordImporter(Sources, Data).ImportAsync(),
            r => Data.Runewords = r);

        await RunImportPhaseAsync("Set",        "setitems.txt",    "sets",         "item",   "Sets",
            () => new SetImporter(Sources, Data).ImportAsync(),
            r => Data.Sets = r);

        await RunImportPhaseAsync("CubeRecipe", "cubemain.txt",    "cube recipes", "recipe", "Cube recipes",
            () => new CubeRecipeImporter(Sources, Data)
                  {
                      EarlyStopSentinelEnabled = EarlyStopSentinelEnabled,
                      CubeRecipeUseDescription = CubeRecipeUseDescription
                  }.ImportAsync(),
            r => Data.CubeRecipes = r);

        await RunImportPhaseAsync("SkillDescription", "skilldesc.txt", "skill descriptions", "skill", "Skill descriptions",
            () => new SkillDescriptionImporter(Data).ImportAsync(),
            r => Data.SkillDescriptions = r.ToDictionary(static set => set.SkillId));

        // 6. Export — the website consumes keyed/*.json + strings/{lang}.json × 13.
        //    manifest.json records output integrity for publish staging.
        Directory.CreateDirectory(_exportPath);
        var outputs = new ExportOutputs(_exportPath);
        _exportSteps.Clear();
        _failedExportSteps.Clear();
        // A manifest describes one complete run. Remove the previous one up front so a
        // run that dies during output leaves no manifest rather than a stale one that
        // vouches for files this run may have replaced or removed.
        outputs.RemovePreviousManifest();

        // Each exporter is its own step: a failure or a missing optional input costs that
        // exporter's output only, never the later exporters or the reports below.
        ReportPhase("Writing keyed JSON bundle...");
        // Skill icons first: keyed/skills.json references only the icons this step wrote.
        Data.ExportedSkillIcons = null;
        await RunExportStepAsync(outputs, SkillIconExporter.Producer, [],
            warn => SkillIconExporter.ExportAsync(outputs, Sources, Data, AssetDiagnostics, warn, Report));
        await RunExportStepAsync(outputs, "keyed-core", KeyedJsonExporter.OutputPaths,
            _ => KeyedJsonExporter.ExportAsync(outputs, Data, PrettyPrintJson, multiLang.ContainsKeyOrFallback));
        await RunExportStepAsync(outputs, "loot-filter.json", [LootFilterExporter.OutputPath],
            warn => LootFilterExporter.ExportAsync(outputs, Sources, multiLang, PrettyPrintJson, warn));
        await RunExportStepAsync(outputs, "drop-calculator.json", [DropCalculatorExporter.OutputPath],
            warn => DropCalculatorExporter.ExportAsync(outputs, Sources, Data, warn));
        await RunExportStepAsync(outputs, "ias-calculator.json", [AttackSpeedExporter.OutputPath],
            warn => AttackSpeedExporter.ExportAsync(outputs, Sources, Data, warn));
        await RunExportStepAsync(outputs, "health-calculator.json", [MonsterHealthExporter.OutputPath],
            warn => MonsterHealthExporter.ExportAsync(outputs, Sources, Data, warn));
        await RunExportStepAsync(outputs, ItemPresentationExporter.Producer, [ItemPresentationExporter.OutputPath],
            warn => ItemPresentationExporter.ExportAsync(outputs, Sources, Data, PrettyPrintJson, AssetDiagnostics, warn, Report));
        await RunExportStepAsync(outputs, "item-stat-presentation.json", [ItemStatPresentationExporter.OutputPath],
            warn => ItemStatPresentationExporter.ExportAsync(outputs, Sources, Data, PrettyPrintJson, warn));
        await RunExportStepAsync(outputs, "rare-names.json", [RareNameExporter.OutputPath],
            warn => RareNameExporter.ExportAsync(outputs, Sources, PrettyPrintJson, warn));

        // Every Sprite/Icon a keyed document references must be a file this run wrote; an
        // earlier run's copy on disk does not count.
        await RunPhaseAsync("Completeness", "asset-references", () => VerifyAssetReferencesAsync(outputs));

        // 6a. Compute the set of every string that actually appears as a value inside
        //     keyed/*.json. The keyed wire format embeds translation keys only in
        //     values (NameStr, KeyedLine.key, nested arg keys), so harvesting all string
        //     values yields a superset of the keys the website will ever look up. We use
        //     this set to:
        //       - promote ONLY referenced CASC entries from the fallback layer into the
        //         per-language bundles (so quest dialogue and other unrelated CASC
        //         strings are not exported);
        //       - scope the missing-translations audit to keys translators can act on.
        var keySources = await CollectKeyReferencesAsync(outputs);
        var referencedKeys = new HashSet<string>(keySources.Keys, StringComparer.Ordinal);
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
        await RunExportStepAsync(outputs, "strings",
            [.. MultiLanguageTranslationService.AllLanguages.Select(LanguageBundleExporter.OutputPath)],
            _ => LanguageBundleExporter.ExportAsync(outputs, multiLang, PrettyPrintJson));

        // 7. Write extras (import report + missing-translations audit).
        ReportPhase("Writing extras (audit reports)...");
        outputs.Producer = "extras";

        // Audit actual display references, including keys no importer happened to probe.
        // The final enUS bundle is the website's fallback for every other language.
        var displayKeySources = await CollectKeyReferencesAsync(outputs, displayOnly: true);
        var missingKeys = FindMissingDisplayKeys(displayKeySources, multiLang);
        ReferencedMissingKeyCount = missingKeys.Count;
        await RunReportStepAsync(outputs, "extras/missing-translations.txt",
            () => WriteMissingKeysReportAsync(outputs, "extras/missing-translations.txt", missingKeys, displayKeySources));

        // Dumps every key whose value(s) had to be sourced from CASC base strings
        // (i.e. referenced by the export but not supplied by the mod's own
        // local\lng\strings\*.json). Useful for deciding whether to fold a small
        // residual set into Config/synthetic-strings.json instead of relying on
        // the bundled CASC fallback. The report is purely informational and does
        // not affect the keyed/strings outputs.
        await RunReportStepAsync(outputs, "extras/casc-fallback-report.txt",
            () => WriteCascFallbackReportAsync(outputs, "extras/casc-fallback-report.txt", multiLang));

        await RunReportStepAsync(outputs, "extras/asset-report.txt",
            () => outputs.WriteTextAsync("extras/asset-report.txt", AssetDiagnostics.Render()));

        await RunReportStepAsync(outputs, "extras/source-report.txt",
            () => outputs.WriteTextAsync("extras/source-report.txt", RenderSourceReport()));

        // The import report is written last among the extras so it also records any
        // failure of the two audits above; nothing after it adds diagnostics.
        await RunReportStepAsync(outputs, "extras/import-report.txt",
            () => outputs.WriteTextAsync("extras/import-report.txt", Result.GenerateReport()));
        outputs.Producer = "";

        // manifest.json is the run's seal: it is written last and lists only files this run
        // produced (with SHA-256). Succeeded = false whenever an error was recorded; Complete
        // is additionally false when any step was skipped or left a declared output
        // unwritten, so a consumer can refuse a partial or mixed bundle.
        await RunPhaseAsync("Export", ExportOutputs.ManifestFileName, () =>
            outputs.WriteManifestAsync(!Result.HasErrors, _exportSteps, PrettyPrintJson));

        if (_failedExportSteps.Count > 0 && !ContinueOnException)
        {
            throw new InvalidOperationException(
                $"Export step(s) failed: {string.Join(", ", _failedExportSteps)}. "
                + "The outputs they attempted were removed; the remaining exports, extras/import-report.txt and "
                + "manifest.json (Succeeded = false) were still written.");
        }

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
    /// (<c>RequiredClass</c>, <c>Class</c>, <c>ClassSpecific</c>, <c>ClassCode</c>), and the
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
        "ClassCode",
        // KeyedLine.Code (camelCase) — raw English propertygroup name on
        // parent lines; siblings PickMode/Children are not strings/objects
        // that contain translation keys, so no extra entries are needed.
        "code",
        "Sprite",
        "Id", "Group", "Condition", "Quality", "Kind", "TreasureClass", "QuestFlag", "QuestFlagEx", "stat",
        "Hash", "Token", "WeaponClass", "TwoHandClass", "Types", "Mode", "Sequence", "IncludeTypes", "ExcludeTypes",
        "RollbackCalc", "HitsCalc", "SelfSpeedCalc",
        "Restrict", "States", "FormTokens",
        "MonProp", "MonsterIds",
        // UnHoarder runtime selectors must remain literal; NameKey is still audited.
        "BaseNameSelector", "TypeNameSelector", "TypeCode", "TypeCode2", "ParentCode", "ParentCode2",
        // Raw English rendered verbatim when the sibling NameKey is absent (the
        // monster-only skills.txt rows, the non-combat monstats.txt rows). Auditing it
        // would re-report exactly the identifiers NameKey was made nullable to drop.
        "FallbackName",
    };

    /// <summary>
    /// Maps every referenced key to the <c>keyed/*.json</c> file names it appears in.
    /// The provenance is what makes <c>missing-translations.txt</c> readable: a key that
    /// only ever shows up in <c>item-stat-presentation.json</c> is a monster-skill
    /// identifier the game itself never translated, not something a translator can fix.
    /// </summary>
    /// <remarks>
    /// Only <c>keyed/*.json</c> files written by <em>this</em> run are harvested. Reading
    /// the directory instead would let a stale document from an earlier export (or from an
    /// exporter that was skipped this time) promote keys into the bundles and the audit.
    /// </remarks>
    internal static async Task<Dictionary<string, SortedSet<string>>> CollectKeyReferencesAsync(ExportOutputs outputs, bool displayOnly = false)
    {
        var referenced = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var keyedFiles = outputs.Files
            .Select(static file => file.Path)
            .Where(static path => path.StartsWith("keyed/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && path.IndexOf('/', "keyed/".Length) < 0)
            .OrderBy(static path => path, StringComparer.Ordinal);

        foreach (var path in keyedFiles)
        {
            await using var stream = File.OpenRead(outputs.GetFullPath(path));
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            CollectStrings(doc.RootElement, Path.GetFileName(path), referenced, displayOnly);
        }

        return referenced;
    }

    /// <summary>Display keys missing from the final enUS bundle (the site's language fallback).</summary>
    internal static List<string> FindMissingDisplayKeys(
        IReadOnlyDictionary<string, SortedSet<string>> references,
        MultiLanguageTranslationService translations)
        => references.Keys.Where(key => !translations.ContainsKey(key))
            .OrderBy(static key => key, StringComparer.Ordinal).ToList();

    private static void CollectStrings(
        System.Text.Json.JsonElement element,
        string sourceFile,
        Dictionary<string, SortedSet<string>> sink,
        bool displayOnly = false,
        bool isDisplayValue = false)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.String:
                var s = element.GetString();
                if ((!displayOnly || isDisplayValue) && !string.IsNullOrEmpty(s))
                {
                    if (!sink.TryGetValue(s, out var files))
                    {
                        files = new SortedSet<string>(StringComparer.Ordinal);
                        sink[s] = files;
                    }
                    files.Add(sourceFile);
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectStrings(item, sourceFile, sink, displayOnly, isDisplayValue);
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
                    var display = isDisplayValue || prop.Name is "key" or "nameKey" or "pluralKey" or "classOnly" or "args" or "CompositeKeys"
                        || prop.Name.EndsWith("Key", StringComparison.Ordinal)
                        // Core item identities are translation keys; type indices are identifiers.
                        || (prop.Name == "Index" && (element.TryGetProperty("Lines", out _)
                            || element.TryGetProperty("SetItems", out _)));
                    CollectStrings(prop.Value, sourceFile, sink, displayOnly, display);
                }
                break;
        }
    }

    /// <summary>
    /// Writes <c>extras/missing-translations.txt</c>: every display key referenced by an
    /// exported keyed document that the final enUS fallback bundle cannot resolve.
    /// </summary>
    private static async Task WriteMissingKeysReportAsync(
        ExportOutputs outputs,
        string exportPath,
        IReadOnlyList<string> missingKeys,
        IReadOnlyDictionary<string, SortedSet<string>> keySources)
    {
        if (missingKeys.Count == 0)
        {
            // Always overwrite any stale report so re-runs reflect the current state.
            await outputs.WriteTextAsync(exportPath, "=== Missing Translation Keys (0) ===\n");
            return;
        }

        var lines = new List<string>
        {
            $"=== Missing Translation Keys ({missingKeys.Count}) ===",
            "Display keys referenced by keyed/*.json that neither the mod's own strings, the",
            "CASC base strings (--casc-data) nor Config/synthetic-strings.json",
            "resolve. Grouped by the exported document that referenced them.",
            "",
            "Scope: explicit display keys, keyed-line arguments and core item identity keys.",
            "Structural identifiers and FallbackName literals are excluded; every language",
            "may fall back to the final enUS bundle.",
            ""
        };

        var groups = missingKeys
            .GroupBy(key => keySources.TryGetValue(key, out var files)
                ? string.Join(", ", files)
                : "(unreferenced)")
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            lines.Add($"--- {group.Key} ({group.Count()}) ---");
            lines.AddRange(group.OrderBy(key => key, StringComparer.Ordinal).Select(key => $"  {key}"));
            lines.Add("");
        }

        await outputs.WriteTextAsync(exportPath, string.Join("\n", lines));
    }

    private void ValidatePaths()
    {
        if (!Directory.Exists(Sources.ModExcelDirectory))
            throw new SourceLayoutException($"Mod Data Directory: '{Sources.ModDataRoot}' has no '{SourceRoots.ExcelDirectory}' folder.");
        if (!Directory.Exists(Sources.ModStringsDirectory))
            throw new SourceLayoutException(
                $"Mod Data Directory: '{Sources.ModDataRoot}' has no '{SourceRoots.StringsDirectory}' folder (expected '{Sources.ModStringsDirectory}'). "
                + "The mod's own strings are the primary translation source.");
    }

    /// <summary>
    /// Checks every <c>Sprite</c> / <c>Icon</c> value in the keyed documents written this run
    /// against the files this run wrote. A dangling reference is an error: the website would
    /// request an image that is either absent or a leftover from another export.
    /// </summary>
    private async Task VerifyAssetReferencesAsync(ExportOutputs outputs)
    {
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in outputs.Files.Select(static f => f.Path)
                     .Where(static p => p.StartsWith("keyed/", StringComparison.OrdinalIgnoreCase)
                                        && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            await using var stream = File.OpenRead(outputs.GetFullPath(path));
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            CollectAssetReferences(doc.RootElement, path, references);
        }

        var dangling = references.Where(r => !outputs.WasWritten(r.Key)).ToList();
        foreach (var group in dangling.GroupBy(static r => r.Value))
        {
            Result.AddError("Completeness", group.Key,
                $"{group.Count()} asset reference(s) do not resolve to a file written by this run. First: "
                + string.Join(", ", group.Select(static r => r.Key).Take(5)));
        }
        Report($"  Asset references: {references.Count} distinct, {dangling.Count} dangling.");
    }

    private static void CollectAssetReferences(System.Text.Json.JsonElement element, string document, Dictionary<string, string> sink)
    {
        switch (element.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectAssetReferences(item, document, sink);
                break;
            case System.Text.Json.JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "Sprite" or "Icon" && property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        && property.Value.GetString() is { Length: > 0 } value)
                    {
                        sink.TryAdd(value, document);
                    }
                    else
                    {
                        CollectAssetReferences(property.Value, document, sink);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// <c>extras/source-report.txt</c>: the two roots and, for every tracked input, the root
    /// that supplied it (or every location searched when none did).
    /// </summary>
    private string RenderSourceReport()
    {
        var lookups = Sources.Lookups;
        var lines = new List<string>
        {
            $"=== Source Report: {lookups.Count} input(s), "
            + $"{lookups.Count(static l => l.Origin == SourceOrigin.Mod)} mod, "
            + $"{lookups.Count(static l => l.Origin == SourceOrigin.Casc)} CASC, "
            + $"{lookups.Count(static l => l.Origin == SourceOrigin.Bundled)} bundled, "
            + $"{lookups.Count(static l => l.Origin == SourceOrigin.None)} not found ===",
        };
        lines.AddRange(Sources.Describe());
        lines.Add("Precedence: the mod's copy, else the CASC copy (bundled reference tables last).");
        lines.Add("");
        foreach (var lookup in lookups)
        {
            lines.Add(lookup.Path is null
                ? $"  [missing] {lookup.RelativePath}  searched {lookup.DescribeSearch()}"
                : $"  [{lookup.Origin.ToString().ToLowerInvariant()}] {lookup.RelativePath}  {lookup.Path}");
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    /// Writes a human-readable list of every translation key that was sourced from
    /// the CASC base strings during the most recent <c>ApplyFallback</c> call.
    /// Each line shows the key followed by the languages whose value came from CASC
    /// (omitted when all 13 languages fell through, which is the common case).
    /// Sorted alphabetically by key for stable diffs across runs.
    /// </summary>
    private static async Task WriteCascFallbackReportAsync(ExportOutputs outputs, string exportPath, MultiLanguageTranslationService multiLang)
    {
        var promotions = multiLang.LastFallbackPromotions;
        if (promotions.Count == 0)
        {
            await outputs.WriteTextAsync(exportPath, "=== CASC-sourced Translation Keys (0) ===\n");
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

        await outputs.WriteTextAsync(exportPath, string.Join("\n", lines));
    }
}
