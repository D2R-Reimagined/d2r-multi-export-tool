// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;
using K = D2RMultiExport.Lib.Translation.SyntheticStringRegistry.Keys;

namespace D2RMultiExport.Tests;

/// <summary>Regression coverage for validated, class-neutral Relic cube recipe summaries.</summary>
public sealed class CubeRecipeAggregationTests
{
    private static readonly string[] ClassCodes = ["ama", "sor", "nec", "pal", "bar", "dru", "ass", "war"];

    private static readonly Tier[] Tiers =
    [
        new("r37", 500, "hp%", 76, 6),
        new("r38", 1_000, "res-mag", 37, 1),
        new("r39", 3_000, "res-all-max", 40, 1)
    ];

    [Fact]
    public async Task CompleteSourceRowsAggregateAndKeepKeysNotesAndStableIdsInKeyedJson()
    {
        var recipes = CreateCompleteRows();
        recipes.Add(new Recipe(["amu"], "tax", 1, Description: "ordinary recipe"));

        var unaggregatedData = await NewDataAsync();
        unaggregatedData.ExportConfig.CubeRecipeClassUpgradeFamilies.Clear();
        unaggregatedData.ExportConfig.BuildLookups();
        AddPropertyMappings(unaggregatedData);
        var unaggregated = await ImportRecipesAsync(unaggregatedData, [.. recipes]);
        Assert.False(unaggregated.HasErrors);
        Assert.Equal(31, unaggregated.Items.Count);

        var data = await NewDataAsync();
        AddPropertyMappings(data);
        var imported = await ImportRecipesAsync(data, [.. recipes]);

        Assert.False(imported.HasErrors);
        Assert.False(imported.HasWarnings);
        Assert.Equal(4, imported.Items.Count);
        Assert.Equal(3, imported.Items.Count(recipe => Tiers.Any(tier => HasInput(recipe, tier.Rune))));

        foreach (var tier in Tiers)
        {
            var summary = Assert.Single(imported.Items, recipe => HasInput(recipe, tier.Rune));
            AssertTierSummary(summary, tier);

            var originalSuccess = Assert.Single(unaggregated.Items, recipe =>
                recipe.Class == "ama" && HasInput(recipe, tier.Rune));
            Assert.Equal(originalSuccess.Id, summary.Id);
        }

        var ordinary = Assert.Single(imported.Items, recipe => HasInput(recipe, "amu"));
        var originalOrdinary = Assert.Single(unaggregated.Items, recipe => HasInput(recipe, "amu"));
        Assert.Equal(originalOrdinary.Id, ordinary.Id);
        Assert.Equal(originalOrdinary.Notes.Select(SerializeLine), ordinary.Notes.Select(SerializeLine));
        Assert.Contains(ordinary.Notes, note => note.Key == K.CubeNoteItemCrafting);

        data.CubeRecipes = imported.Items;
        var outputRoot = Directory.CreateTempSubdirectory("d2r-relic-cube-json-").FullName;
        try
        {
            var outputs = new ExportOutputs(outputRoot);
            await KeyedJsonExporter.ExportAsync(outputs, data, prettyPrint: false);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputRoot, "keyed", "cube-recipes.json")));
            var recipesJson = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(4, recipesJson.Length);

            foreach (var tier in Tiers)
            {
                var summary = Assert.Single(imported.Items, recipe => HasInput(recipe, tier.Rune));
                var serialized = Assert.Single(recipesJson, recipe => recipe.GetProperty("Id").GetString() == summary.Id);
                var output = serialized.GetProperty("Outputs").GetProperty("A");
                var outputLines = output.GetProperty("Lines").EnumerateArray().ToArray();
                Assert.Contains(outputLines, line =>
                    line.GetProperty("key").GetString() == K.CubeOutputClassSkillLevels
                    && line.GetProperty("args")[0].GetInt32() == 1);
                Assert.Contains(outputLines, line =>
                    line.GetProperty("key").GetString() == StatKey(tier.StatCode)
                    && line.GetProperty("args")[0].GetInt32() == 5);
                var noteLines = serialized.GetProperty("Notes").EnumerateArray().ToArray();
                Assert.Contains(noteLines, line => line.GetProperty("key").GetString() == K.CubeNoteUberCharmUpgrade);
                Assert.Contains(noteLines, line =>
                    line.GetProperty("key").GetString() == K.CubeNoteRelicKillRequirement
                    && line.GetProperty("args")[0].GetInt32() == tier.Kills);
                Assert.Contains(noteLines, line => line.GetProperty("key").GetString() == K.CubeNoteRelicUpgradeRefund);
                Assert.Equal(output.GetProperty("Name").GetRawText(), serialized.GetProperty("Inputs")[0].GetProperty("Name").GetRawText());
            }
        }
        finally
        {
            Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RecipesSharingOnlyOrbAndTierRuneAreNotFamilyMembers()
    {
        // Real cubemain.txt rows (feature/endgame-phase1, feature/maps) combine a weapon with
        // ooe and a Relic tier rune; they must neither join nor invalidate a Relic tier.
        Recipe[] weaponRows =
        [
            new(["weap", "ooe", "ooc", "r34", "r38", "jewl,uni"], "useitem", Op: 16),
            new(["weap", "ooe", "ooc", "r35", "r37", "jewl,uni"], "useitem", Op: 16),
            new(["weap", "ooe", "ooc", "r36", "r39", "jewl,uni"], "useitem", Op: 16)
        ];
        var recipes = CreateCompleteRows();
        recipes.InsertRange(0, weaponRows);

        var unaggregatedData = await NewDataAsync();
        unaggregatedData.ExportConfig.CubeRecipeClassUpgradeFamilies.Clear();
        unaggregatedData.ExportConfig.BuildLookups();
        AddPropertyMappings(unaggregatedData);
        var unaggregated = await ImportRecipesAsync(unaggregatedData, [.. recipes]);

        var data = await NewDataAsync();
        AddPropertyMappings(data);
        var imported = await ImportRecipesAsync(data, [.. recipes]);

        Assert.False(imported.HasErrors);
        Assert.False(imported.HasWarnings);
        Assert.Equal(33, unaggregated.Items.Count);
        Assert.Equal(6, imported.Items.Count);
        foreach (var tier in Tiers)
        {
            var summary = Assert.Single(imported.Items, recipe =>
                HasInput(recipe, "Relic of the Ancients") && HasInput(recipe, tier.Rune));
            AssertTierSummary(summary, tier);
        }

        var weaponIds = unaggregated.Items.Where(recipe => HasInput(recipe, "weap")).Select(static recipe => recipe.Id).ToArray();
        Assert.Equal(3, weaponIds.Length);
        Assert.Equal(weaponIds, imported.Items.Where(recipe => HasInput(recipe, "weap")).Select(static recipe => recipe.Id));
    }

    [Fact]
    public async Task IncompleteAndConflictingTiersStayIntactWhileAnotherTierAggregates()
    {
        var recipes = CreateCompleteRows();
        recipes.RemoveAt(recipes.FindIndex(recipe => HasInput(recipe, "r37") && recipe.Class == "ama"));
        var conflictingStatIndex = recipes.FindIndex(recipe => HasInput(recipe, "r38") && recipe.Class == "ama");
        recipes[conflictingStatIndex] = recipes[conflictingStatIndex] with { Mod2Min = 6, Mod2Max = 6 };

        var data = await NewDataAsync();
        AddPropertyMappings(data);
        var imported = await ImportRecipesAsync(data, [.. recipes]);

        Assert.False(imported.HasErrors);
        Assert.True(imported.HasWarnings);
        Assert.Equal(20, imported.Items.Count);
        Assert.Equal(9, imported.Items.Count(recipe => HasInput(recipe, "r37")));
        Assert.Equal(10, imported.Items.Count(recipe => HasInput(recipe, "r38")));
        Assert.Single(imported.Items, recipe => HasInput(recipe, "r39"));
        Assert.Contains(imported.Errors, error => error.Message.Contains("class successes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(imported.Errors, error => error.Message.Contains("conflicting shared stat modifiers", StringComparison.OrdinalIgnoreCase));
        Assert.Single(imported.Items, recipe => HasInput(recipe, "r39") && HasGenericClassBonus(recipe));
    }

    [Fact]
    public async Task IngredientMismatchAndUnexpectedRefundGuardPreserveTheirFamilies()
    {
        var recipes = CreateCompleteRows();
        var mismatchedIngredientIndex = recipes.FindIndex(recipe => HasInput(recipe, "r37") && recipe.Class == "ama");
        recipes[mismatchedIngredientIndex] = recipes[mismatchedIngredientIndex] with
        {
            Inputs = ["Relic of the Ancients", "r01", "r37"]
        };
        var badGuardIndex = recipes.FindIndex(recipe => HasInput(recipe, "r38") && recipe.Op == 4);
        recipes[badGuardIndex] = recipes[badGuardIndex] with { Param = 439 };
        var badRefundIndex = recipes.FindIndex(recipe => HasInput(recipe, "r39") && recipe.Op == 15);
        recipes[badRefundIndex] = recipes[badRefundIndex] with { OutputB = "r01" };

        var data = await NewDataAsync();
        AddPropertyMappings(data);
        var imported = await ImportRecipesAsync(data, [.. recipes]);

        Assert.False(imported.HasErrors);
        Assert.True(imported.HasWarnings);
        Assert.Equal(30, imported.Items.Count);
        Assert.Equal(10, imported.Items.Count(recipe => HasInput(recipe, "r37")));
        Assert.Equal(10, imported.Items.Count(recipe => HasInput(recipe, "r38")));
        Assert.Equal(10, imported.Items.Count(recipe => HasInput(recipe, "r39")));
        Assert.Contains(imported.Errors, error => error.Message.Contains("raw input tokens", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(imported.Errors, error => error.Message.Contains("expected Param=438", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(imported.Errors, error => error.Message.Contains("non-Relic ingredient", StringComparison.OrdinalIgnoreCase));
    }

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

    private static void AddPropertyMappings(GameData data)
    {
        data.Stats["item_addclassskills"] = new StatEntry
        {
            Stat = "item_addclassskills",
            DescriptionFunction = 13,
            DescStrPosKey = "testClassSkill"
        };
        foreach (var classCode in ClassCodes)
        {
            data.Properties[classCode] = new PropertyEntry
            {
                Code = classCode,
                Stat1 = "item_addclassskills",
                Function1 = 1
            };
        }
        foreach (var tier in Tiers)
        {
            data.Properties[tier.StatCode] = new PropertyEntry
            {
                Code = tier.StatCode,
                Stat1 = tier.StatCode,
                Function1 = 1
            };
            data.Stats[tier.StatCode] = new StatEntry
            {
                Stat = tier.StatCode,
                IsSynthetic = true,
                DescriptionFunction = 2,
                DescStrPosKey = StatKey(tier.StatCode)
            };
        }
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
                if (value != null)
                    cells[columnIndices[column]] = value;
            }

            Set("description", recipe.Description);
            Set("enabled", "1");
            Set("class", recipe.Class);
            Set("op", recipe.Op?.ToString());
            Set("param", recipe.Param?.ToString());
            Set("value", recipe.Value?.ToString());
            Set("numinputs", recipe.NumInputs?.ToString() ?? recipe.Inputs.Length.ToString());
            for (var i = 0; i < recipe.Inputs.Length; i++)
                Set($"input {i + 1}", recipe.Inputs[i]);
            Set("output", recipe.Output);
            Set("output b", recipe.OutputB);
            Set("output c", recipe.OutputC);
            Set("mod 1", recipe.Mod1);
            Set("mod 1 min", recipe.Mod1Min?.ToString());
            Set("mod 1 max", recipe.Mod1Max?.ToString());
            Set("mod 1 chance", recipe.Mod1Chance?.ToString());
            Set("mod 2", recipe.Mod2);
            Set("mod 2 min", recipe.Mod2Min?.ToString());
            Set("mod 2 max", recipe.Mod2Max?.ToString());
            Set("mod 2 chance", recipe.Mod2Chance?.ToString());
            return string.Join('\t', cells);
        }

        var root = Directory.CreateTempSubdirectory("d2r-cube-aggregation-").FullName;
        try
        {
            var excel = Directory.CreateDirectory(Path.Combine(root, "global", "excel")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "local", "lng", "strings"));
            await File.WriteAllLinesAsync(Path.Combine(excel, "cubemain.txt"), [header[0], .. recipes.Select(Row)]);
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

    private static List<Recipe> CreateCompleteRows()
    {
        List<Recipe> recipes = [];
        foreach (var tier in Tiers)
        {
            string[] inputs = ["Relic of the Ancients", "ooe", tier.Rune];
            recipes.Add(new Recipe(inputs, "useitem", Op: 15, Param: tier.AlreadyUpgradedParam, Value: tier.AlreadyUpgradedValue,
                OutputB: "ooe", OutputC: tier.Rune, Description: "opaque row label"));
            recipes.Add(new Recipe(inputs, "useitem", Op: 4, Param: 438, Value: tier.Kills - 1,
                OutputB: "ooe", OutputC: tier.Rune, Description: "another opaque label"));
            foreach (var classCode in ClassCodes)
            {
                recipes.Add(new Recipe(inputs, "useitem", Class: classCode, Mod1: classCode, Mod1Min: 1, Mod1Max: 1, Mod1Chance: 100,
                    Mod2: tier.StatCode, Mod2Min: 5, Mod2Max: 5, Mod2Chance: 100, Description: "success row"));
            }
        }
        return recipes;
    }

    private static void AssertTierSummary(CubeRecipeExport recipe, Tier tier)
    {
        Assert.Equal(["Relic of the Ancients", "ooe", tier.Rune], recipe.Inputs.Select(static input => input.RawToken));
        Assert.Equal(3, recipe.NumInputs);
        Assert.Equal("", recipe.Class);
        Assert.Null(recipe.ClassLine);
        Assert.Equal("", recipe.RequiredClass);
        Assert.Equal(K.CubeRelicUpgradeTitle, recipe.Description);
        Assert.Equal("useitem", recipe.Outputs.A!.MainToken);
        Assert.Null(recipe.Outputs.B);
        Assert.Null(recipe.Outputs.C);
        Assert.Equal(recipe.Inputs[0].NameLine?.Key, recipe.Outputs.A.NameLine?.Key);

        var lines = recipe.Outputs.A.Properties.SelectMany(static property => property.Lines).ToArray();
        var classBonus = Assert.Single(lines, line => line.Key == K.CubeOutputClassSkillLevels);
        Assert.Equal(new object[] { 1 }, classBonus.Args);
        Assert.DoesNotContain(lines, line => line.Key == "testClassSkill" || ClassCodes.Contains(line.Key, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(recipe.Outputs.A.Properties, property =>
            property.PropertyCode == tier.StatCode && property.Min == 5 && property.Max == 5);
        var sharedBonus = Assert.Single(lines, line => line.Key == StatKey(tier.StatCode));
        Assert.Equal(new object[] { 5 }, sharedBonus.Args);

        Assert.Contains(recipe.Notes, note => note.Key == K.CubeNoteUberCharmUpgrade);
        var killNote = Assert.Single(recipe.Notes, note => note.Key == K.CubeNoteRelicKillRequirement);
        Assert.Equal(new object[] { tier.Kills }, killNote.Args);
        Assert.Contains(recipe.Notes, note => note.Key == K.CubeNoteRelicUpgradeRefund);
    }

    private static bool HasInput(CubeRecipeExport recipe, string code) =>
        recipe.Inputs.Any(input => string.Equals(input.RawToken, code, StringComparison.OrdinalIgnoreCase));

    private static bool HasInput(Recipe recipe, string code) =>
        recipe.Inputs.Any(input => string.Equals(input, code, StringComparison.OrdinalIgnoreCase));

    private static bool HasGenericClassBonus(CubeRecipeExport recipe) =>
        recipe.Outputs.A?.Properties.SelectMany(static property => property.Lines)
            .Any(line => line.Key == K.CubeOutputClassSkillLevels) == true;

    private static string StatKey(string code) => $"test_{code}";

    private static string SerializeLine(KeyedLine line) => JsonSerializer.Serialize(line);

    private sealed record Tier(string Rune, int Kills, string StatCode, int AlreadyUpgradedParam, int AlreadyUpgradedValue);

    private sealed record Recipe(
        string[] Inputs,
        string Output,
        int? Op = null,
        int? Param = null,
        int? Value = null,
        string? Class = null,
        string? OutputB = null,
        string? OutputC = null,
        int? NumInputs = null,
        string? Mod1 = null,
        int? Mod1Min = null,
        int? Mod1Max = null,
        int? Mod1Chance = null,
        string? Mod2 = null,
        int? Mod2Min = null,
        int? Mod2Max = null,
        int? Mod2Chance = null,
        string? Description = null);
}
