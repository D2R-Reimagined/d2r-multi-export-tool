// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using D2RMultiExport.Lib;

// ── Required path arguments ─────────────────────────────────────────────────────
// Every input is derived from these two directories by D2RMultiExport.Lib.SourceRoots.
var modDataOption = new Option<string>("--mod-data", "-m")
{
    Description = "The mod's data directory (contains global\\excel and local\\lng\\strings). The mod repository root that contains data\\ is accepted too.",
    Required = true
};

var cascDataOption = new Option<string?>("--casc-data", "-c")
{
    Description = "The extracted vanilla D2R data directory (data\\ or CascView's nested data\\data\\; the extraction root is accepted too). "
        + "Supplies every file the mod does not ship: HD sprites, base strings, vanilla tables. Strongly recommended."
};

var exportOption = new Option<string>("--export", "--out", "-o")
{
    Description = "Path for generated export",
    Required = true
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
    Description = "Include the raw English `Description` column from CubeMain.txt on each cube recipe (documented exception #1 to the keyed-only rule)",
    DefaultValueFactory = _ => false
};
var continueOnExceptionOption = new Option<bool>("--continue-on-exception")
{
    Description = "Continue past per-phase failures instead of aborting the run (failures are still recorded in extras\\import-report.txt)",
    DefaultValueFactory = _ => false
};

var exportCommand = new Command("export", "Import game data and export the key-based JSON bundle (keyed/*.json + strings/{lang}.json × 13)")
{
    modDataOption, cascDataOption, exportOption, configOption,
    destructiveOption, prettyOption,
    earlyStopOption, cubeRecipeDescOption, continueOnExceptionOption
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var modData      = parseResult.GetValue(modDataOption)!;
    var cascData     = parseResult.GetValue(cascDataOption);
    var export       = parseResult.GetValue(exportOption)!;
    var config       = parseResult.GetValue(configOption);

    D2RMultiExportPipeline pipeline;
    try
    {
        pipeline = new D2RMultiExportPipeline(modData, cascData, export, config)
        {
            PrettyPrintJson           = parseResult.GetValue(prettyOption),
            EarlyStopSentinelEnabled  = parseResult.GetValue(earlyStopOption),
            CubeRecipeUseDescription  = parseResult.GetValue(cubeRecipeDescOption),
            ContinueOnException       = parseResult.GetValue(continueOnExceptionOption),
            DestructiveExport         = parseResult.GetValue(destructiveOption),
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

        if (pipeline.Result.HasErrors)
        {
            Console.WriteLine(pipeline.Result.GenerateReport());
            if (pipeline.DestructiveExport)
                Console.Error.WriteLine($"Destructive export: '{Path.GetFullPath(export)}' was not replaced (see the errors above).");
            return 1;
        }

        Console.WriteLine("Export completed successfully.");
        if (pipeline.DestructiveExport)
        {
            // Without errors the replacement succeeded; a failed replacement is a recorded error.
            Console.WriteLine($"  Destructive export: '{Path.GetFullPath(export)}' now holds only this export.");
        }
        if (pipeline.MultiLangTranslations is { } ml)
        {
            Console.WriteLine($"  enUS keys loaded: {ml.Count}");
        }
        if (pipeline.ReferencedMissingKeyCount > 0)
        {
            Console.WriteLine($"  {pipeline.ReferencedMissingKeyCount} missing translation key(s) referenced in keyed/*.json — see extras\\missing-translations.txt");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fatal error: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        return 2;
    }
});

// ── publish: stage only the files the last run vouched for ─────────────────────────
var publishFromOption = new Option<string>("--from", "--out", "-o")
{
    Description = "The export output directory (contains manifest.json)",
    Required = true
};
var publishToOption = new Option<string>("--staging", "-s")
{
    Description = "A new or empty directory to receive the publishable bundle (never inside --from)",
    Required = true
};
var allowIncompleteOption = new Option<bool>("--allow-incomplete")
{
    Description = "Stage a run whose manifest says Complete: false (a skipped exporter). Failed runs are never staged.",
    DefaultValueFactory = _ => false
};
var publishCommand = new Command("publish",
    "Copy exactly the files listed in manifest.json (re-verified by SHA-256) into a staging directory. "
    + "Refuses failed runs, incomplete runs unless --allow-incomplete, and files changed since the run. "
    + "The output directory itself is never modified.")
{
    publishFromOption, publishToOption, allowIncompleteOption
};
publishCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var result = await D2RMultiExport.Lib.Exporters.PublishStager.StageAsync(
        parseResult.GetValue(publishFromOption)!,
        parseResult.GetValue(publishToOption)!,
        parseResult.GetValue(allowIncompleteOption));
    foreach (var problem in result.Problems)
        (result.Staged ? Console.Out : Console.Error).WriteLine(problem);
    if (!result.Staged)
    {
        Console.Error.WriteLine("No completed bundle was staged.");
        return 4;
    }
    Console.WriteLine($"Staged {result.Files.Count} file(s).");
    return 0;
});

var rootCommand = new RootCommand("D2R Reimagined mod data exporter — generates the key-based, language-agnostic JSON bundle consumed by the D2R Reimagined website.")
{
    exportCommand,
    publishCommand
};

return await rootCommand.Parse(args).InvokeAsync();
