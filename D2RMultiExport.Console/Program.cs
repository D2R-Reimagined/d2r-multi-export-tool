// SPDX-License-Identifier: GPL-3.0-or-later

using System.CommandLine;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Comparison;

// ── Required path arguments ─────────────────────────────────────────────────────
// Every input is derived from these two directories by D2RMultiExport.Lib.SourceRoots.
var modDataOption = new Option<string>("--mod-data", "-m")
{
    Description =
        "The mod's data directory (contains global\\excel and local\\lng\\strings). The mod repository root that contains data\\ is accepted too.",
    Required = true
};

var cascDataOption = new Option<string?>("--casc-data", "-c")
{
    Description =
        "The extracted vanilla D2R data directory (data\\ or CascView's nested data\\data\\; the extraction root is accepted too). "
        + "Supplies every file the mod does not ship: HD sprites, base strings, vanilla tables. Strongly recommended."
};

var exportOption = new Option<string?>("--export", "--out", "-o")
{
    Description = "Path for generated export (required unless --dry-run is given)"
};

var configOption = new Option<string?>("--config")
{
    Description = "Path to config directory (default: bundled Config/)"
};

// ── Output options ──────────────────────────────────────────────────────────────
var destructiveOption = new Option<bool>("--destructive")
{
    Description = "Replace ALL existing contents of --out with only this export, after the run finished without errors "
                  + "(refused for drive roots, repository roots and similar locations). Default: overwrite matching files and leave the rest untouched.",
    DefaultValueFactory = _ => false
};
var dryRunOption = new Option<bool>("--dry-run")
{
    Description =
        "Run the full export into a scratch directory and discard it, including its extras reports; --out is never touched. "
        + "Only a log of every error and warning (dryrun-log.txt in --dry-run-logs) is kept, when the run recorded any.",
    DefaultValueFactory = _ => false
};
var dryRunLogsOption = new Option<string?>("--dry-run-logs")
{
    Description =
        "Directory for the dry-run log (default: test-output\\dryrun of the repository containing the exporter, "
        + "else below the exporter's folder)"
};
var prettyOption = new Option<bool>("--pretty")
{
    Description = "Pretty-print JSON output",
    DefaultValueFactory = _ => true
};

// ── Pipeline-tuning toggles (mirror the GUI-exposed switches in MainWindowViewModel) ──
var earlyStopOption = new Option<bool>("--early-stop")
{
    Description = "Stop importing CubeMain.txt at the first sentinel row (faster smoke runs)",
    DefaultValueFactory = _ => true
};
var cubeRecipeDescOption = new Option<bool>("--cube-recipe-descriptions")
{
    Description =
        "Include the raw English `Description` column from CubeMain.txt on each cube recipe (documented exception #1 to the keyed-only rule)",
    DefaultValueFactory = _ => false
};
var continueOnExceptionOption = new Option<bool>("--continue-on-exception")
{
    Description =
        "Continue past per-phase failures instead of aborting the run (failures are still recorded in extras\\import-report.txt)",
    DefaultValueFactory = _ => false
};

var exportCommand = new Command("export",
    "Import game data and export the key-based JSON bundle (keyed/*.json + strings/{lang}.json × 13)")
{
    modDataOption, cascDataOption, exportOption, configOption,
    destructiveOption, dryRunOption, dryRunLogsOption, prettyOption,
    earlyStopOption, cubeRecipeDescOption, continueOnExceptionOption
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var modData = parseResult.GetValue(modDataOption)!;
    var cascData = parseResult.GetValue(cascDataOption);
    var export = parseResult.GetValue(exportOption);
    var config = parseResult.GetValue(configOption);
    var dryRun = parseResult.GetValue(dryRunOption);

    if (!dryRun && string.IsNullOrWhiteSpace(export))
    {
        Console.Error.WriteLine("Option '--out' is required unless --dry-run is given.");
        return 1;
    }

    if (dryRun && parseResult.GetValue(destructiveOption))
    {
        Console.Error.WriteLine("--dry-run and --destructive cannot be combined: a dry run never touches --out.");
        return 3;
    }

    D2RMultiExportPipeline pipeline;
    try
    {
        pipeline = new D2RMultiExportPipeline(modData, cascData, export, config)
        {
            PrettyPrintJson = parseResult.GetValue(prettyOption),
            EarlyStopSentinelEnabled = parseResult.GetValue(earlyStopOption),
            CubeRecipeUseDescription = parseResult.GetValue(cubeRecipeDescOption),
            ContinueOnException = parseResult.GetValue(continueOnExceptionOption),
            DestructiveExport = parseResult.GetValue(destructiveOption),
            DryRun = dryRun,
            DryRunLogDirectory = parseResult.GetValue(dryRunLogsOption),
        };
    }
    catch (SourceLayoutException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }

    if (pipeline.DestructiveExport && pipeline.FindDestructiveExportDanger() is { } danger)
    {
        Console.Error.WriteLine($"--destructive refused: {danger}");
        return 3;
    }

    foreach (var line in pipeline.Sources.Describe()) Console.WriteLine(line);

    try
    {
        await pipeline.RunAsync();

        if (pipeline.DryRun)
        {
            // Unlike a real run, warnings are listed too: the dry run is how they are reviewed.
            if (pipeline.Result.AllErrors.Count > 0)
                Console.WriteLine(pipeline.Result.GenerateReport());
            Console.WriteLine($"  {pipeline.Result.ErrorCount} error(s), {pipeline.Result.WarningCount} warning(s), "
                              + $"{pipeline.ReferencedMissingKeyCount} missing translation key(s).");
            if (pipeline.Result.HasErrors)
            {
                Console.Error.WriteLine(
                    $"Dry run finished with {pipeline.Result.ErrorCount} error(s); the export was discarded. Log: '{pipeline.DryRunLogFilePath}'.");
                return 1;
            }

            Console.WriteLine(pipeline.DryRunLogFilePath is { } warningLog && File.Exists(warningLog)
                ? $"Dry run completed without errors; the export was discarded. Warnings: '{warningLog}'."
                : "Dry run completed without errors or warnings; the export was discarded and no log was written.");
            return 0;
        }

        if (pipeline.Result.HasErrors)
        {
            Console.WriteLine(pipeline.Result.GenerateReport());
            if (pipeline.DestructiveExport)
                Console.Error.WriteLine(
                    $"Destructive export: '{Path.GetFullPath(export!)}' was not replaced (see the errors above).");
            return 1;
        }

        Console.WriteLine("Export completed successfully.");
        if (pipeline.DestructiveExport)
        {
            // Without errors the replacement succeeded; a failed replacement is a recorded error.
            Console.WriteLine($"  Destructive export: '{Path.GetFullPath(export!)}' now holds only this export.");
        }

        if (pipeline.MultiLangTranslations is { } ml)
        {
            Console.WriteLine($"  enUS keys loaded: {ml.Count}");
        }

        if (pipeline.ReferencedMissingKeyCount > 0)
        {
            Console.WriteLine(
                $"  {pipeline.ReferencedMissingKeyCount} missing translation key(s) referenced in keyed/*.json — see extras\\missing-translations.txt");
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fatal error: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        if (pipeline.DryRun)
        {
            // Includes the dry run's own warnings, for example a scratch directory that could not be deleted.
            Console.WriteLine(pipeline.Result.GenerateReport());
            if (pipeline.DryRunLogFilePath is { } log && File.Exists(log))
                Console.Error.WriteLine($"Dry run aborted; the export was discarded. Log: '{log}'.");
        }

        return 2;
    }
});

var compareOldOption = new Option<string>("--old")
{
    Description = "Directory containing the old export snapshot and manifest.json",
    Required = true
};
var compareNewOption = new Option<string>("--new")
{
    Description = "Directory containing the new export snapshot and manifest.json",
    Required = true
};
var compareOutputOption = new Option<string>("--out", "-o")
{
    Description = "Directory where comparison JSON and Markdown reports will be written",
    Required = true
};
var comparePrettyOption = new Option<bool>("--pretty")
{
    Description = "Pretty-print changes.json",
    DefaultValueFactory = _ => true
};

var compareCommand = new Command("compare",
    "Compare two manifest-backed export snapshots and write a structured change report.")
{
    compareOldOption, compareNewOption, compareOutputOption, comparePrettyOption
};

compareCommand.SetAction(async (parseResult, _) =>
{
    var oldDirectory = parseResult.GetValue(compareOldOption)!;
    var newDirectory = parseResult.GetValue(compareNewOption)!;
    var outputDirectory = parseResult.GetValue(compareOutputOption)!;

    try
    {
        var conflict = SnapshotComparison.FindOutputConflict(oldDirectory, newDirectory, outputDirectory);
        if (conflict is not null)
        {
            Console.Error.WriteLine($"Comparison refused: {conflict}");
            return 3;
        }

        var report = await SnapshotComparison.CompareAsync(oldDirectory, newDirectory);
        var recipeMarkdown = await SnapshotComparison.RenderRecipeChangesAsync(report);
        var itemMarkdown = await SnapshotComparison.RenderItemChangesAsync(report);
        var itemPatchNotesMarkdown = await SnapshotComparison.RenderItemPatchNotesAsync(report);
        await SnapshotComparison.WriteAsync(report, outputDirectory, parseResult.GetValue(comparePrettyOption));
        await SnapshotComparison.WriteRecipeChangesAsync(recipeMarkdown, outputDirectory);
        await SnapshotComparison.WriteItemChangesAsync(itemMarkdown, outputDirectory);
        await SnapshotComparison.WriteItemPatchNotesAsync(itemPatchNotesMarkdown, outputDirectory);
        foreach (var category in report.Categories)
        {
            Console.WriteLine(
                $"{category.Category} comparison: {category.Status}; added {category.Counts.Added}, removed {category.Counts.Removed}, modified {category.Counts.Modified}, unchanged {category.Counts.Unchanged}.");
            if (category.NestedCounts is { } nestedCounts)
            {
                Console.WriteLine(
                    $"  {category.NestedField} comparison: added {nestedCounts.Added}, removed {nestedCounts.Removed}, modified {nestedCounts.Modified}, unchanged {nestedCounts.Unchanged}.");
            }

            var ambiguousAffixWarnings =
                category.Warnings.Count(static issue => issue.Code == "ambiguous-affix-identity");
            if (ambiguousAffixWarnings > 0)
                Console.WriteLine(
                    $"  {ambiguousAffixWarnings} ambiguous affix identity warning(s); unresolved variants are additions/removals.");
        }

        Console.WriteLine($"Comparison report: '{Path.GetFullPath(outputDirectory)}\\changes.json'.");
        Console.WriteLine($"Recipe Markdown report: '{Path.GetFullPath(outputDirectory)}\\recipe-changes.md'.");
        Console.WriteLine($"Item Markdown report: '{Path.GetFullPath(outputDirectory)}\\item-changes.md'.");
        Console.WriteLine($"Item patch notes: '{Path.GetFullPath(outputDirectory)}\\item-patch-notes.md'.");
        if (!report.Succeeded)
        {
            Console.Error.WriteLine("Comparison completed with validation or comparison errors; see changes.json.");
            return 1;
        }

        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Comparison failed: {ex.Message}");
        return 2;
    }
});

var rootCommand = new RootCommand("D2R Reimagined mod data exporter and snapshot comparison tool.")
{
    exportCommand,
    compareCommand
};

return await rootCommand.Parse(args).InvokeAsync();
