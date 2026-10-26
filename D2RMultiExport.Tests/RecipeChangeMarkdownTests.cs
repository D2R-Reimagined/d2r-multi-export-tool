// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Focused regressions for the recipe comparison Markdown report.</summary>
public sealed class RecipeChangeMarkdownTests : IDisposable
{
    private static readonly string[] CategoryFiles =
    [
        "keyed/cube-recipes.json", "keyed/uniques.json", "keyed/sets.json", "keyed/runewords.json",
        "keyed/armors.json", "keyed/weapons.json", "keyed/magicprefix.json", "keyed/magicsuffix.json"
    ];

    private readonly string _root = Directory.CreateTempSubdirectory("d2r-recipe-markdown-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddedRemovedModifiedAndUnchangedRecipesRenderWithStableIds()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            "[{\"Id\":\"removed-id\",\"Index\":1,\"Description\":\"removed recipe\"},"
            + "{\"Id\":\"modified-id\",\"Index\":2,\"Description\":\"before\"},"
            + "{\"Id\":\"same-id\",\"Index\":3,\"Description\":\"same\"}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            "[{\"Id\":\"added-id\",\"Index\":4,\"Description\":\"added recipe\"},"
            + "{\"Id\":\"modified-id\",\"Index\":5,\"Description\":\"after\",\"Value\":2},"
            + "{\"Id\":\"same-id\",\"Index\":9,\"Description\":\"same\"}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var markdown = await SnapshotComparison.RenderRecipeChangesAsync(report);

        Assert.Contains("### Recipe `added-id` (row 4)", markdown);
        Assert.Contains("### Recipe `removed-id` (row 1)", markdown);
        Assert.Contains("### Recipe `modified-id` (row 2 → 5)", markdown);
        Assert.Contains("Description", markdown);
        Assert.Contains("1 added, 1 removed, 1 modified, 1 unchanged, 2 reindexed", markdown);
        Assert.DoesNotContain("### Recipe `same-id`", markdown);
    }

    [Fact]
    public async Task OldAndNewRecordsUseTheirOwnSnapshotTranslations()
    {
        const string recipes =
            "[{\"Id\":\"changed\",\"Index\":1,\"Inputs\":[{\"Quantity\":2,\"Name\":{\"key\":\"itemKey\",\"args\":[]}}],"
            + "\"Outputs\":{\"A\":{\"Quantity\":1,\"Name\":{\"key\":\"resultKey\",\"args\":[]}}}}]";
        const string changedRecipes =
            "[{\"Id\":\"changed\",\"Index\":1,\"Inputs\":[{\"Quantity\":2,\"Name\":{\"key\":\"itemKey\",\"args\":[]}}],"
            + "\"Outputs\":{\"A\":{\"Quantity\":2,\"Name\":{\"key\":\"resultKey\",\"args\":[]}}}}]";
        var oldSnapshot = await CreateSnapshotAsync("old",
            recipes,
            "{\"itemKey\":\"Old Ingredient\",\"resultKey\":\"Old Result\"}");
        var newSnapshot = await CreateSnapshotAsync("new",
            changedRecipes,
            "{\"itemKey\":\"New Ingredient\",\"resultKey\":\"New Result\"}");

        var markdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Old Ingredient", markdown);
        Assert.Contains("Old Result", markdown);
        Assert.Contains("New Ingredient", markdown);
        Assert.Contains("New Result", markdown);
        Assert.Contains("English translations (old snapshot): available (manifest-verified)", markdown);
        Assert.Contains("English translations (new snapshot): available (manifest-verified)", markdown);
    }

    [Fact]
    public async Task MissingTranslationKeysFallBackToKeyAndOriginalArguments()
    {
        var recipes =
            "[{\"Id\":\"recipe\",\"Index\":1,\"Inputs\":[{\"Quantity\":1,\"Name\":{\"key\":\"missingItemKey\",\"args\":[7]}}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", "{}");
        var newSnapshot = await CreateSnapshotAsync("new", recipes, "{}");

        var markdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("missingItemKey \\[7\\]", markdown);
    }

    [Fact]
    public async Task MissingUnlistedAndHashMismatchedTranslationFilesAreNotTrusted()
    {
        const string recipes =
            "[{\"Id\":\"new\",\"Index\":1,\"Inputs\":[{\"Quantity\":1,\"Name\":{\"key\":\"itemKey\",\"args\":[3]}}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", includeStrings: false);
        var unlistedSnapshot =
            await CreateSnapshotAsync("unlisted", recipes, "{\"itemKey\":\"Untrusted\"}", stringsListed: false);
        var actualStrings = "{\"itemKey\":\"Changed after manifest\"}";
        var hashMismatchSnapshot = await CreateSnapshotAsync("mismatch", recipes, actualStrings,
            stringsHashOverride: Sha256(Encoding.UTF8.GetBytes("{\"itemKey\":\"Prior content\"}")));

        var unlistedMarkdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, unlistedSnapshot));
        var mismatchMarkdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, hashMismatchSnapshot));

        Assert.Contains("English translations (old snapshot): unavailable", unlistedMarkdown);
        Assert.Contains("itemKey \\[3\\]", unlistedMarkdown);
        Assert.Contains("not in manifest", unlistedMarkdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("English translations (new snapshot): unavailable", mismatchMarkdown);
        Assert.Contains("itemKey \\[3\\]", mismatchMarkdown);
        Assert.Contains("SHA-256 mismatch", mismatchMarkdown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NumericRangesPropertyGroupsAndClassOnlyAreRenderedWithoutChangingValues()
    {
        const string recipes = "[{\"Id\":\"recipe\",\"Index\":1,\"Class\":{\"key\":\"classKey\",\"args\":[]},"
                               + "\"Outputs\":{\"A\":{\"Quantity\":1,\"Name\":{\"key\":\"itemKey\",\"args\":[]},\"Lines\":["
                               + "{\"key\":\"rangeKey\",\"args\":[1,3]},"
                               + "{\"key\":\"percentKey\",\"args\":[0.375],\"classOnly\":\"classOnlyKey\"},"
                               + "{\"key\":\"\",\"args\":[],\"nameKey\":\"groupKey\",\"code\":\"raw-group-id\",\"pickMode\":\"2\","
                               + "\"children\":[{\"key\":\"childKey\",\"args\":[0.375],\"chance\":25}]}]}}}]";
        const string translations =
            "{\"classKey\":\"Sorceress\",\"itemKey\":\"Crafted Item\",\"rangeKey\":\"%+d Damage\","
            + "\"percentKey\":\"Bonus %d%%\",\"classOnlyKey\":\"Amazon Only\",\"groupKey\":\"Random Group\","
            + "\"childKey\":\"Modifier %d\"}";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", translations);
        var newSnapshot = await CreateSnapshotAsync("new", recipes, translations);

        var markdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("\\+(1-3) Damage", markdown);
        Assert.Contains("Bonus 0.375%", markdown);
        Assert.Contains("Amazon Only", markdown);
        Assert.Contains("Random Group", markdown);
        Assert.Contains("raw-group-id", markdown);
        Assert.Contains("pickMode=\"2\"", markdown);
        Assert.Contains("chance=25", markdown);
        Assert.Contains("Modifier 0.375", markdown);
    }

    [Fact]
    public async Task UnusualQualifiersAndOutputShapesRemainReadable()
    {
        const string recipes =
            "[{\"Id\":\"odd\",\"Index\":8,\"Inputs\":[{\"Quantity\":\"many\",\"RawToken\":\"unusual-token\",\"Qualifiers\":\"unexpected-qualifier-shape\"}],"
            + "\"Outputs\":\"not-an-output-object\"}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]");
        var newSnapshot = await CreateSnapshotAsync("new", recipes);

        var markdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("<missing Name>", markdown);
        Assert.Contains("unusual-token", markdown);
        Assert.Contains("unexpected-qualifier-shape", markdown);
        Assert.Contains("not-an-output-object", markdown);
        Assert.Contains("Complete original record JSON", markdown);
    }

    [Fact]
    public async Task MarkdownRenderingIsDeterministic()
    {
        const string recipes = "[{\"Id\":\"stable\",\"Index\":4,\"Inputs\":[{\"Quantity\":1,\"RawToken\":\"token\"}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", "[]");
        var newSnapshot = await CreateSnapshotAsync("new", recipes);
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        var first = await SnapshotComparison.RenderRecipeChangesAsync(report);
        var second = await SnapshotComparison.RenderRecipeChangesAsync(report);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task InvalidCubeCategoryDoesNotClaimThereWereZeroChanges()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "{\"Id\":\"not-an-array\"}");
        var newSnapshot = await CreateSnapshotAsync("new", "[]");

        var markdown = await SnapshotComparison.RenderRecipeChangesAsync(
            await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot));

        Assert.Contains("Cube recipe comparison: **NotCompared**", markdown);
        Assert.Contains("were not successfully compared", markdown);
        Assert.Contains("does not mean that there were no recipe changes", markdown);
        Assert.Contains("Recipe counts: unavailable", markdown);
        Assert.DoesNotContain("## Added recipes", markdown);
        Assert.DoesNotContain("## Removed recipes", markdown);
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string cubeRecipes,
        string strings = "{}",
        bool includeStrings = true,
        bool stringsListed = true,
        string? stringsHashOverride = null)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var files = new List<object>();
        foreach (var relativePath in CategoryFiles)
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

        var steps = new List<object>
        {
            new
            {
                Producer = "keyed-core",
                Status = "Written",
                Outputs = CategoryFiles,
                NotWritten = Array.Empty<string>()
            }
        };
        if (includeStrings)
        {
            const string relativePath = "strings/enUS.json";
            var path = Path.Combine(snapshot, "strings", "enUS.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(strings);
            await File.WriteAllBytesAsync(path, bytes);
            steps.Add(new
            {
                Producer = "strings",
                Status = "Written",
                Outputs = new[] { relativePath },
                NotWritten = Array.Empty<string>()
            });
            if (stringsListed)
            {
                files.Add(new
                {
                    Path = relativePath,
                    Bytes = bytes.LongLength,
                    Sha256 = stringsHashOverride ?? Sha256(bytes),
                    Producer = "strings"
                });
            }
        }

        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = true,
            Complete = includeStrings && stringsListed,
            Steps = steps,
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
