// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>
/// Base-item names in cube recipes prefer code-specific translations and fall back to NameStr.
/// </summary>
public sealed class CubeRecipeNameResolutionTests
{
    private static GameData NewData()
    {
        var config = new ExportConfig();
        config.BuildLookups();
        config.CubeIngredientLabels.Input["uni"] = "Unique Item";
        config.CubeIngredientLabels.Output["uni"] = "Unique Item";
        return new GameData
        {
            ExportConfig = config,
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService()
        };
    }

    private static async Task<ImportResult<CubeRecipeExport>> ImportRecipesAsync(
        GameData data,
        params (string Output, string Input)[] recipes)
    {
        var header = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "PropertyRanges", "data", "global", "excel", "cubemain.txt"));
        var columns = header[0].Split('\t');
        string Row(string output, string input)
        {
            var cells = new string[columns.Length];
            Array.Fill(cells, "");
            cells[Array.IndexOf(columns, "enabled")] = "1";
            cells[Array.IndexOf(columns, "numinputs")] = "1";
            cells[Array.IndexOf(columns, "input 1")] = input;
            cells[Array.IndexOf(columns, "output")] = output;
            return string.Join('\t', cells);
        }

        var root = Directory.CreateTempSubdirectory("d2r-cube-name-").FullName;
        try
        {
            var excel = Directory.CreateDirectory(Path.Combine(root, "global", "excel")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "local", "lng", "strings"));
            await File.WriteAllLinesAsync(Path.Combine(excel, "cubemain.txt"),
                [header[0], .. recipes.Select(recipe => Row(recipe.Output, recipe.Input))]);
            return await new CubeRecipeImporter(SourceRoots.Create(root, null), data)
            {
                EarlyStopSentinelEnabled = false
            }.ImportAsync();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CodeSpecificTranslationIsUsedForLatentSunderRecipeInputAndOutput()
    {
        var data = NewData();
        data.Translations.MergeSeed(new Dictionary<string, string>
        {
            ["cs1"] = "Latent Sunder Charm",
            ["cm3"] = "Grand Charm"
        });
        data.MiscItems["cs1"] = new MiscEntry { Code = "cs1", NameStr = "cm3" };

        var result = await ImportRecipesAsync(data, ("cs1,uni", "cs1,uni,qty=3"));

        var recipe = Assert.Single(result.Items);
        var input = Assert.Single(recipe.Inputs);
        Assert.Equal("Latent Sunder Charm", input.Name);
        Assert.Equal("cs1", input.NameLine?.Key);
        Assert.Equal("Latent Sunder Charm", recipe.Outputs.A?.Name);
        Assert.Equal("cs1", recipe.Outputs.A?.NameLine?.Key);
    }

    [Fact]
    public async Task NameStrTranslationIsUsedWhenCodeHasNoTranslation()
    {
        var data = NewData();
        data.Translations.MergeSeed(new Dictionary<string, string>
        {
            ["strFallback"] = "Fallback Item"
        });
        data.MiscItems["xyz"] = new MiscEntry { Code = "xyz", NameStr = "strFallback" };

        var result = await ImportRecipesAsync(data, ("xyz,uni", "xyz,uni,qty=3"));

        var recipe = Assert.Single(result.Items);
        var input = Assert.Single(recipe.Inputs);
        Assert.Equal("Fallback Item", input.Name);
        Assert.Equal("strFallback", input.NameLine?.Key);
        Assert.Equal("Fallback Item", recipe.Outputs.A?.Name);
        Assert.Equal("strFallback", recipe.Outputs.A?.NameLine?.Key);
    }
}