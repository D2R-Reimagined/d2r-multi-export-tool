// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Exporters;

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

var websiteOption = new Option<string?>("--website", "-w")
{
    Description = "Website checkout (contains package.json and static\\). After a successful, complete export its static\\data "
        + "is validated and replaced with exactly this run's files. Nothing is committed or pushed."
};

// ── Output options ──────────────────────────────────────────────────────────────
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
    modDataOption, cascDataOption, exportOption, configOption, websiteOption,
    prettyOption,
    earlyStopOption, cubeRecipeDescOption, continueOnExceptionOption
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var modData      = parseResult.GetValue(modDataOption)!;
    var cascData     = parseResult.GetValue(cascDataOption);
    var export       = parseResult.GetValue(exportOption)!;
    var config       = parseResult.GetValue(configOption);
    var website      = parseResult.GetValue(websiteOption);
    if (!string.IsNullOrWhiteSpace(website))
    {
        var (validWebsite, websiteMessage) = WebsiteDataInstaller.ValidateWebsiteRoot(website);
        if (!validWebsite || WebsiteDataInstaller.OverlapsDestination(export, website))
        {
            Console.Error.WriteLine(validWebsite
                ? $"--out must not be inside '{WebsiteDataInstaller.DestinationFor(website)}' (or contain it); the website copy is installed from --out."
                : websiteMessage);
            return 3;
        }
    }

    D2RMultiExportPipeline pipeline;
    try
    {
        pipeline = new D2RMultiExportPipeline(modData, cascData, export, config)
        {
            PrettyPrintJson           = parseResult.GetValue(prettyOption),
            EarlyStopSentinelEnabled  = parseResult.GetValue(earlyStopOption),
            CubeRecipeUseDescription  = parseResult.GetValue(cubeRecipeDescOption),
            ContinueOnException       = parseResult.GetValue(continueOnExceptionOption),
        };
    }
    catch (SourceLayoutException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 3;
    }
    foreach (var line in pipeline.Sources.Describe()) Console.WriteLine(line);

    try
    {
        await pipeline.RunAsync();

        if (pipeline.Result.HasErrors)
        {
            Console.WriteLine(pipeline.Result.GenerateReport());
            if (!string.IsNullOrWhiteSpace(website))
                Console.Error.WriteLine("Website data was not updated: the export recorded errors.");
            return 1;
        }

        Console.WriteLine("Export completed successfully.");
        if (pipeline.MultiLangTranslations is { } ml)
        {
            Console.WriteLine($"  enUS keys loaded: {ml.Count}");
        }
        if (pipeline.ReferencedMissingKeyCount > 0)
        {
            Console.WriteLine($"  {pipeline.ReferencedMissingKeyCount} missing translation key(s) referenced in keyed/*.json — see extras\\missing-translations.txt");
        }
        if (string.IsNullOrWhiteSpace(website)) return 0;

        var install = await WebsiteDataInstaller.InstallAsync(export, website);
        foreach (var warning in install.Warnings) Console.WriteLine($"Warning: {warning}");
        if (!install.Succeeded)
        {
            Console.Error.WriteLine($"Website installation refused: {install.Message}");
            foreach (var problem in install.Problems) Console.Error.WriteLine($"  - {problem}");
            return 5;
        }
        Console.WriteLine(install.Status == WebsiteInstallStatus.Unchanged
            ? $"Website data is already up to date: {install.Destination}"
            : $"Website data installed: {install.Destination} ({install.Plan!.Summary})");
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
