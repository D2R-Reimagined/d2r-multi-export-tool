// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Focused regressions for player-facing recipe patch notes.</summary>
public sealed class RecipePatchNotesMarkdownTests : IDisposable
{
    private static readonly string[] CategoryFiles =
    [
        "keyed/cube-recipes.json", "keyed/uniques.json", "keyed/sets.json", "keyed/runewords.json",
        "keyed/armors.json", "keyed/weapons.json", "keyed/magicprefix.json", "keyed/magicsuffix.json"
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("d2r-recipe-patch-notes-").FullName;

    [Fact]
    public async Task MatchedChangesDescribeIngredientsRestrictionsAndOutputs()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", MatchedRecipe("Amazon", 5, "magic", "out", 1, 100, 10, 1),
            Translations);
        var newSnapshot = await CreateSnapshotAsync("new", MatchedRecipe("Sorceress", 3, "rare", "out2", 2, 50, 15, 2),
            Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("## Item Crafting Recipe", markdown);
        Assert.Contains("Required ingredient quantity: 3 × Amulet (was 5 × Amulet)", markdown);
        Assert.Contains("Added ingredient requirement: Rare Item", markdown);
        Assert.Contains("Removed ingredient requirement: Magic Item", markdown);
        Assert.Contains("Required class restriction: Sorceress (was Amazon)", markdown);
        Assert.Contains("Output item (A): Socketed Armor (was Rare Jewel)", markdown);
        Assert.Contains("Output quantity (A): 2 × Socketed Armor (was 1 × Rare Jewel)", markdown);
        Assert.Contains("Output chance (A): 50 (was 100)", markdown);
        Assert.Contains("Output property (A): +15% Enhanced Damage (was +10%)", markdown);
        Assert.Contains("**Review:** Recipe Op changed (1 → 2)", markdown);
        Assert.Contains("Sorceress-only recipe", markdown);
    }

    [Fact]
    public async Task UnmatchedRecipesShowReadableDefinitionsAndDoNotGuessPairs()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", RecipeWithId("removed-id", "Old recipe", "r01", 1),
            Translations);
        var newSnapshot = await CreateSnapshotAsync("new", RecipeWithId("added-id", "New recipe", "gem", 2),
            Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("## Recipe identity and unmatched definitions", markdown);
        Assert.Contains("not automatically paired", markdown);
        Assert.Contains("### Added recipe definitions", markdown);
        Assert.Contains("### Removed recipe definitions", markdown);
        Assert.Contains("- Required ingredients:", markdown);
        Assert.Contains("2 × Perfect Ruby", markdown);
        Assert.Contains("1 × El Rune", markdown);
        Assert.Contains("- Results:", markdown);
        Assert.Contains("1 × Rare Jewel", markdown);
        Assert.Contains("<!-- changes.json: cube-recipes / added-id -->", markdown);
        Assert.Contains("- Source: `added-id`, row 1.", markdown);
        Assert.DoesNotContain("#### Recipe `added-id`", markdown);
        Assert.DoesNotContain("```json", markdown);
        Assert.DoesNotContain("Complete original record JSON", markdown);

        var outputDirectory = Directory.CreateDirectory(Path.Combine(_root, "comparison")).FullName;
        await SnapshotComparison.WriteRecipePatchNotesAsync(markdown, outputDirectory);
        Assert.Equal(markdown, await File.ReadAllTextAsync(Path.Combine(outputDirectory, "recipe-patch-notes.md")));
    }

    [Fact]
    public async Task MissingTranslationsAndOperationChangesRemainExplicitlyForReview()
    {
        const string oldRecipe = "[{\"Id\":\"matched\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
                                 "\"Inputs\":[{\"Name\":{\"key\":\"missingName\",\"args\":[5]},\"Quantity\":1," +
                                 "\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"missingOutput\",\"args\":[]}," +
                                 "\"Quantity\":1,\"Qualifiers\":[],\"Lines\":[]}},\"Op\":1,\"Param\":7,\"Value\":2}]";
        const string newRecipe = "[{\"Id\":\"matched\",\"Index\":1,\"NumInputs\":1,\"ResolvedInputsCount\":1," +
                                 "\"Inputs\":[{\"Name\":{\"key\":\"missingName\",\"args\":[5]},\"Quantity\":1," +
                                 "\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"missingOutput\",\"args\":[]}," +
                                 "\"Quantity\":1,\"Qualifiers\":[],\"Lines\":[]}},\"Op\":2,\"Param\":8,\"Value\":2}]";
        var oldSnapshot = await CreateSnapshotAsync("old", oldRecipe, "{}");
        var newSnapshot = await CreateSnapshotAsync("new", newRecipe, "{}");
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("missingName \\[5\\] (translation unavailable)", markdown);
        Assert.Contains("missingOutput \\[\\] (translation unavailable)", markdown);
        Assert.Contains("**Review: translation unavailable or unsupported.**", markdown);
        Assert.Contains("**Review:** Recipe Op changed (1 → 2); its gameplay meaning has not been verified.", markdown);
        Assert.Contains("**Review:** Recipe Param changed (7 → 8)", markdown);
        Assert.Contains("Untranslated or unsupported values requiring review:", markdown);
    }

    [Fact]
    public async Task GroupedIngredientQuantitiesAreNotRepeatedAsRequirementsOrCountErrors()
    {
        const string cubeRecipes = "[{\"Id\":\"socket-punch\",\"Index\":15882,\"NumInputs\":5," +
                                   "\"ResolvedInputsCount\":2,\"Inputs\":[{\"Name\":{\"key\":\"weapon\",\"args\":[]}," +
                                   "\"Quantity\":1,\"Qualifiers\":[]},{\"Name\":{\"key\":\"jewel\",\"args\":[]}," +
                                   "\"Quantity\":4,\"Qualifiers\":[{\"key\":\"strCubeQualifierQuantityN\",\"args\":[4]}]}]," +
                                   "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"useitem\",\"args\":[]}," +
                                   "\"Quantity\":1,\"Qualifiers\":[{\"key\":\"strCubeQualifierItemWithSocketsN\"," +
                                   "\"args\":[4]}],\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes, Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var markdown = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Contains("1 × Any Weapon + 4 × Jewel", markdown);
        Assert.Contains("Result requirement: Socketed (4)", markdown);
        Assert.DoesNotContain("Quantity (4)", markdown);
        Assert.Contains("Review items: 0.", markdown);
    }

    [Fact]
    public async Task UnavailableCategoriesAreNotReportedAsZeroAndOutputIsDeterministic()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", Translations, includeCube: false);
        var newSnapshot = await CreateSnapshotAsync("new", "[]", Translations);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderRecipePatchNotesAsync(report);
        var second = await SnapshotComparison.RenderRecipePatchNotesAsync(report);

        Assert.Equal(first, second);
        Assert.Contains("Recipe changes unavailable", first);
        Assert.Contains("Recipe change counts: unavailable", first);
        Assert.DoesNotContain("Total matched modifications: 0", first);
        Assert.DoesNotContain("| Other recipes | 0 | 0 | 0 |", first);
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string cubeRecipes,
        string translations,
        bool includeCube = true)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var includedCategoryFiles = CategoryFiles.Where(path => includeCube || path != "keyed/cube-recipes.json")
            .ToArray();
        var files = new List<object>();
        foreach (var relativePath in includedCategoryFiles)
        {
            var content = relativePath == "keyed/cube-recipes.json" ? cubeRecipes : "[]";
            var path = Path.Combine(snapshot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(path, bytes);
            files.Add(new
            {
                Path = relativePath,
                Bytes = bytes.LongLength,
                Sha256 = Sha256(bytes),
                Producer = "keyed-core"
            });
        }

        const string stringsPath = "strings/enUS.json";
        var stringsFilePath = Path.Combine(snapshot, "strings", "enUS.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stringsFilePath)!);
        var stringsBytes = Encoding.UTF8.GetBytes(translations);
        await File.WriteAllBytesAsync(stringsFilePath, stringsBytes);
        files.Add(new
        {
            Path = stringsPath,
            Bytes = stringsBytes.LongLength,
            Sha256 = Sha256(stringsBytes),
            Producer = "strings"
        });

        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = true,
            Complete = true,
            Steps = new object[]
            {
                new
                {
                    Producer = "keyed-core",
                    Status = "Written",
                    Outputs = includedCategoryFiles,
                    NotWritten = Array.Empty<string>()
                },
                new
                {
                    Producer = "strings",
                    Status = "Written",
                    Outputs = new[] { stringsPath },
                    NotWritten = Array.Empty<string>()
                }
            },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static string MatchedRecipe(
        string requiredClass,
        int ingredientQuantity,
        string ingredientQualifier,
        string output,
        int outputQuantity,
        int outputChance,
        int enhancedDamage,
        int operation) =>
        "[{\"Id\":\"matched\",\"Index\":1,\"RequiredClass\":\"" + requiredClass + "\",\"NumInputs\":" +
        (ingredientQuantity + 1) + "," +
        "\"ResolvedInputsCount\":2,\"Inputs\":[{\"Name\":{\"key\":\"amu\",\"args\":[]},\"Quantity\":" +
        ingredientQuantity + ",\"Qualifiers\":[{\"key\":\"" + ingredientQualifier +
        "\",\"args\":[]}],\"RawToken\":\"amu\"}," +
        "{\"Name\":{\"key\":\"r01\",\"args\":[]},\"Quantity\":1,\"Qualifiers\":[],\"RawToken\":\"r01\"}]," +
        "\"Outputs\":{\"A\":{\"Name\":{\"key\":\"" + output + "\",\"args\":[]},\"Quantity\":" +
        outputQuantity + ",\"Qualifiers\":[],\"OutputChance\":" + outputChance + ",\"Lines\":[{\"key\":\"enhanced\"," +
        "\"args\":[" + enhancedDamage + "]}]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}],\"Op\":" +
        operation + ",\"Param\":7,\"Value\":1}]";

    private static string RecipeWithId(string id, string description, string ingredient, int quantity) =>
        "[{\"Id\":\"" + id + "\",\"Index\":1,\"Description\":\"" + description + "\",\"NumInputs\":" +
        quantity + "," +
        "\"ResolvedInputsCount\":1,\"Inputs\":[{\"Name\":{\"key\":\"" + ingredient + "\",\"args\":[]}," +
        "\"Quantity\":" + quantity +
        ",\"Qualifiers\":[]}],\"Outputs\":{\"A\":{\"Name\":{\"key\":\"out\",\"args\":[]}," +
        "\"Quantity\":1,\"Qualifiers\":[],\"OutputChance\":100,\"Lines\":[]}},\"Notes\":[{\"key\":\"category\",\"args\":[]}]}]";

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private const string Translations = "{\"category\":\"Item Crafting Recipe\",\"amu\":\"Amulet\"," +
                                        "\"r01\":\"El Rune\",\"r02\":\"Eld Rune\",\"gem\":\"Perfect Ruby\"," +
                                        "\"out\":\"Rare Jewel\",\"out2\":\"Socketed Armor\",\"magic\":\"Magic Item\"," +
                                        "\"rare\":\"Rare Item\",\"enhanced\":\"%+d%% Enhanced Damage\"," +
                                        "\"weapon\":\"Any Weapon\",\"jewel\":\"Jewel\",\"useitem\":\"Return Exact Input Item\"," +
                                        "\"strCubeQualifierItemWithSocketsN\":\"Socketed (%d)\"," +
                                        "\"strCubeQualifierQuantityN\":\"Quantity (%d)\"}";

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
