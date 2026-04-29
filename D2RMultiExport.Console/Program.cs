// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using D2RMultiExport.Lib;

// ── Required path arguments ─────────────────────────────────────────────────────
var excelOption = new Option<string>("--excel", "-e")
{
    Description = "Path to the .txt data files (excel directory)",
    Required = true
};

// `--translations` is kept as a back-compat alias so existing scripts continue to work.
var modStringsOption = new Option<string>("--mod-strings", "--translations", "-t")
{
    Description = "Path to the mod's local\\lng\\strings directory (.json translation files; mod overrides win over CASC base)",
    Required = true
};

var baseStringsOption = new Option<string?>("--base-strings", "-b")
{
    Description = "Optional path to the read-only CASC base strings dir (used to fill keys missing from the mod overrides). Example: C:\\z_Casc\\data\\data\\local\\lng\\strings"
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
    excelOption, modStringsOption, baseStringsOption, exportOption, configOption,
    prettyOption,
    earlyStopOption, cubeRecipeDescOption, continueOnExceptionOption
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var excel        = parseResult.GetValue(excelOption)!;
    var modStrings   = parseResult.GetValue(modStringsOption)!;
    var baseStrings  = parseResult.GetValue(baseStringsOption);
    var export       = parseResult.GetValue(exportOption)!;
    var config       = parseResult.GetValue(configOption);

    var pipeline = new D2RMultiExportPipeline(excel, modStrings, export, config)
    {
        PrettyPrintJson           = parseResult.GetValue(prettyOption),
        BaseStringsPath           = baseStrings,
        EarlyStopSentinelEnabled  = parseResult.GetValue(earlyStopOption),
        CubeRecipeUseDescription  = parseResult.GetValue(cubeRecipeDescOption),
        ContinueOnException       = parseResult.GetValue(continueOnExceptionOption),
    };

    try
    {
        await pipeline.RunAsync();

        if (pipeline.Result.HasErrors)
        {
            Console.WriteLine(pipeline.Result.GenerateReport());
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
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Fatal error: {ex.Message}");
        Console.Error.WriteLine(ex.ToString());
        return 2;
    }
});

var rootCommand = new RootCommand("D2R Reimagined mod data exporter — generates the key-based, language-agnostic JSON bundle consumed by the D2R Reimagined website.")
{
    exportCommand
};

return await rootCommand.Parse(args).InvokeAsync();
