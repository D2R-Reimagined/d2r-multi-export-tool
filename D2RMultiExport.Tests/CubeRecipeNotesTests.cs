// SPDX-License-Identifier: GPL-3.0-or-later

using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using K = D2RMultiExport.Lib.Translation.SyntheticStringRegistry.Keys;

namespace D2RMultiExport.Tests;

/// <summary>
/// Cube recipe notes are matched from source recipe structure, not descriptions or export positions.
/// </summary>
public sealed class CubeRecipeNotesTests
{
    private static async Task<GameData> NewDataAsync()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "Config", "export-config.json");
        var config = await ExportConfig.LoadAsync(configPath);
        return new GameData
        {
            ExportConfig = config,
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService()
        };
    }

    private static async Task<ImportResult<CubeRecipeExport>> ImportRecipesAsync(GameData data, params Recipe[] recipes)
    {
        var header = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "PropertyRanges", "data", "global", "excel", "cubemain.txt"));
        var columns = header[0].Split('\t');
        var columnIndices = columns
            .Select((column, index) => (column, index))
            .ToDictionary(pair => pair.column, pair => pair.index, StringComparer.OrdinalIgnoreCase);

        string Row(Recipe recipe)
        {
            var cells = new string[columns.Length];
            Array.Fill(cells, "");

            void Set(string column, string? value)
            {
                if (value != null) cells[columnIndices[column]] = value;
            }

            Set("description", recipe.Description);
            Set("enabled", "1");
            Set("op", recipe.Op?.ToString());
            Set("param", recipe.Param?.ToString());
            Set("value", recipe.Value?.ToString());
            Set("numinputs", recipe.NumInputs?.ToString() ?? recipe.Inputs.Length.ToString());
            for (var i = 0; i < recipe.Inputs.Length; i++)
                Set($"input {i + 1}", recipe.Inputs[i]);
            Set("output", recipe.Output);
            Set("output b", recipe.OutputB);
            Set("mod 1", recipe.Mod1);
            return string.Join('\t', cells);
        }

        var root = Directory.CreateTempSubdirectory("d2r-cube-notes-").FullName;
        try
        {
            var excel = Directory.CreateDirectory(Path.Combine(root, "global", "excel")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "local", "lng", "strings"));
            await File.WriteAllLinesAsync(Path.Combine(excel, "cubemain.txt"),
                [header[0], .. recipes.Select(Row)]);
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

    private static void AssertHasNote(CubeRecipeExport recipe, string noteKey) =>
        Assert.Contains(recipe.Notes, note => note.Key == noteKey);

    private static void AssertDoesNotHaveNote(CubeRecipeExport recipe, string noteKey) =>
        Assert.DoesNotContain(recipe.Notes, note => note.Key == noteKey);

    [Fact]
    public async Task RenewalRerollsAreRerollsWithoutRemovingLegitimateForceWhiteNotes()
    {
        var data = await NewDataAsync();
        var renewalRecipes = Enumerable.Range(0, 10)
            .Select(index => new Recipe(
                ["oor,qty=5", index < 5 ? "ooc" : "ooa"], "usetype,reg", 17, 442, 7, Value: index))
            .ToArray();
        var result = await ImportRecipesAsync(data,
        [
            .. renewalRecipes,
            new Recipe(["oor,qty=4", "ooc"], "usetype,reg", 17, 442, 7, Value: 10),
            new Recipe(["oor,qty=5", "ooc"], "usetype,nor", 17, 442, 7, Value: 11),
            new Recipe(["armo", "oor"], "usetype,hiq", 1, null, 2),
            new Recipe(["amu"], "usetype,nor", 1, 386, 1),
            new Recipe(["rin", "rin", "rin"], "usetype,mag", 1, null, 3)
        ]);

        Assert.Empty(result.Errors);
        Assert.Equal(15, result.Items.Count);
        foreach (var recipe in result.Items.Take(10))
        {
            AssertHasNote(recipe, K.CubeNoteRerollItem);
            AssertDoesNotHaveNote(recipe, K.CubeNoteForceWhite);
        }

        AssertDoesNotHaveNote(result.Items[10], K.CubeNoteRerollItem);
        AssertHasNote(result.Items[10], K.CubeNoteForceWhite);
        AssertDoesNotHaveNote(result.Items[11], K.CubeNoteRerollItem);
        AssertHasNote(result.Items[11], K.CubeNoteForceWhite);
        AssertHasNote(result.Items[12], K.CubeNoteForceWhite);
        AssertHasNote(result.Items[13], K.CubeNoteRerollItem);
        AssertHasNote(result.Items[14], K.CubeNoteRerollItem);
    }

    [Fact]
    public async Task RelicUpgradeRulesCoverAllRunesAndRefundVariantsOnlyWhenAllInputsMatch()
    {
        var data = await NewDataAsync();
        var relicRecipes = new List<Recipe>();
        foreach (var rune in new[] { "r37", "r38", "r39" })
        {
            for (var variant = 0; variant < 10; variant++)
            {
                relicRecipes.Add(new Recipe(
                    ["Relic of the Ancients", "ooe", rune],
                    variant % 2 == 0 ? "usetype" : "r01",
                    1,
                    null,
                    3,
                    OutputB: variant % 2 == 0 ? null : "Relic of the Ancients",
                    Value: variant));
            }
        }

        var result = await ImportRecipesAsync(data,
        [
            .. relicRecipes,
            new Recipe(["Relic of the Ancients", "ooe"], "usetype", 1, null, 2),
            new Recipe(["Relic of the Ancients", "r37"], "usetype", 1, null, 2)
        ]);

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.All(result.Errors, error => Assert.Equal(ImportErrorSeverity.Warning, error.Severity));
        Assert.Equal(32, result.Items.Count);
        foreach (var recipe in result.Items.Take(30))
            AssertHasNote(recipe, K.CubeNoteUberCharmUpgrade);
        AssertDoesNotHaveNote(result.Items[30], K.CubeNoteUberCharmUpgrade);
        AssertDoesNotHaveNote(result.Items[31], K.CubeNoteUberCharmUpgrade);
    }

    [Fact]
    public async Task ForcedAffixAndThrowingWeaponOutputsAreCraftingRecipes()
    {
        var data = await NewDataAsync();
        var forcedAffixRecipes = new[]
        {
            new Recipe(["amu"], "amu,mag,pre=123", 1),
            new Recipe(["rin"], "rin,mag,pre=124", 1, Value: 1),
            new Recipe(["rin"], "rin,mag,pre=125", 1, Value: 2),
            new Recipe(["rin"], "rin,mag,pre=126", 1, Value: 3),
            new Recipe(["rin"], "rin,mag,pre=127", 1, Value: 4),
            new Recipe(["weap"], "weap,mag,suf=128", 1),
            new Recipe(["swor"], "swor,mag,suf=129", 1)
        };
        var result = await ImportRecipesAsync(data,
        [
            .. forcedAffixRecipes,
            new Recipe(["r01"], "tax", 1, Value: 5),
            new Recipe(["r01"], "jav", 1, Value: 6),
            new Recipe(["jew"], "usetype,mag", 1, Mod1: "sock"),
            new Recipe(["amu,mag,pre=123"], "usetype", 1)
        ]);

        Assert.Empty(result.Errors);
        Assert.Equal(11, result.Items.Count);
        foreach (var recipe in result.Items.Take(10))
            AssertHasNote(recipe, K.CubeNoteItemCrafting);
        AssertDoesNotHaveNote(result.Items[10], K.CubeNoteItemCrafting);
    }

    [Fact]
    public async Task ArrowAndBoltNormalizersAreForceWhiteButMagicOutputsAreNot()
    {
        var data = await NewDataAsync();
        var result = await ImportRecipesAsync(data,
            new Recipe(["aqv", "r01", "r15"], "aqv,nor", 1),
            new Recipe(["cqv", "r01", "r15"], "cqv,nor", 1),
            new Recipe(["r01", "r15"], "usetype,nor", 1),
            new Recipe(["aqv", "r01", "r15"], "usetype,mag", 1, Value: 1));

        Assert.Empty(result.Errors);
        Assert.Equal(4, result.Items.Count);
        foreach (var recipe in result.Items.Take(3))
            AssertHasNote(recipe, K.CubeNoteForceWhite);
        AssertDoesNotHaveNote(result.Items[3], K.CubeNoteForceWhite);
    }

    [Fact]
    public async Task CraftedSunderConversionsRequireMatchingCraftedAndPrecraftedTokens()
    {
        var data = await NewDataAsync();
        data.Translations.MergeSeed(new Dictionary<string, string>
        {
            ["strColdRupture"] = "Cold Rupture"
        });
        data.MiscItems["cs1"] = new MiscEntry { Code = "cs1", NameStr = "strColdRupture" };

        var families = new[]
        {
            "Cold Rupture", "Flame Rift", "Crack of the Heavens",
            "Rotting Fissure", "Bone Break", "Black Cleft"
        };
        var conversionRecipes = families
            .Select(family => new Recipe(
                [$"Crafted {family}"], "usetype", 1, 386, 1, $"PreCrafted {family}"))
            .ToArray();
        var result = await ImportRecipesAsync(data,
        [
            .. conversionRecipes,
            new Recipe(["cs1"], "usetype", 1, 386, 1),
            new Recipe(["Crafted Cold Rupture"], "usetype", 1, 386, 1, "PreCrafted Flame Rift", Value: 2),
            new Recipe(["Crafted Cold Rupture"], "usetype", 1, 386, 1, Value: 1)
        ]);

        Assert.Empty(result.Errors);
        Assert.Equal(9, result.Items.Count);
        foreach (var recipe in result.Items.Take(7))
            AssertHasNote(recipe, K.CubeNoteSunderItemCrafting);
        AssertDoesNotHaveNote(result.Items[7], K.CubeNoteSunderItemCrafting);
        AssertDoesNotHaveNote(result.Items[8], K.CubeNoteSunderItemCrafting);
    }

    private sealed record Recipe(
        string[] Inputs,
        string Output,
        int? Op,
        int? Param = null,
        int? NumInputs = null,
        string? OutputB = null,
        int? Value = null,
        string? Description = null,
        string? Mod1 = null);
}
