// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Manifest validation and keyed snapshot comparison regressions.</summary>
public sealed class SnapshotComparisonTests : IDisposable
{
    private const string CubeRecipePath = "keyed/cube-recipes.json";
    private const string UniquePath = "keyed/uniques.json";
    private const string SetPath = "keyed/sets.json";
    private static readonly string[] CategoryFiles = [CubeRecipePath, UniquePath, SetPath];
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-snapshot-comparison-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task CubeRecipesStillMatchByStableIdAndIgnoreRowIndexShifts()
    {
        var oldSnapshot =
            await CreateSnapshotAsync("old", cubeRecipes: "[{\"Id\":\"same\",\"Index\":1,\"Enabled\":true}]");
        var newSnapshot =
            await CreateSnapshotAsync("new", cubeRecipes: "[{\"Id\":\"same\",\"Index\":7,\"Enabled\":true}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var cube = GetCategory(report, "cube-recipes");

        Assert.True(report.Succeeded);
        Assert.Equal(1, cube.Counts.Unchanged);
        Assert.Equal(1, cube.Counts.Reindexed);
        Assert.Equal(0, cube.Counts.Modified);
    }

    [Fact]
    public async Task CubeRecipeAddRemoveAndStructuralModificationArePreserved()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            cubeRecipes:
            "[{\"Id\":\"removed\",\"Index\":1},{\"Id\":\"changed\",\"Outputs\":{\"A\":{\"Quantity\":1}}}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            cubeRecipes: "[{\"Id\":\"added\",\"Index\":4},{\"Id\":\"changed\",\"Outputs\":{\"A\":{\"Quantity\":2}}}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var cube = GetCategory(report, "cube-recipes");

        Assert.Equal((1, 1, 1), (cube.Counts.Added, cube.Counts.Removed, cube.Counts.Modified));
        Assert.Equal("added", Assert.Single(cube.AddedRecords).Key);
        Assert.Equal("removed", Assert.Single(cube.RemovedRecords).Key);
        Assert.Equal("Outputs.A.Quantity", Assert.Single(Assert.Single(cube.ModifiedRecords).Changes).Path);
    }

    [Fact]
    public async Task CubeFormattingAndObjectPropertyOrderDoNotCreateChanges()
    {
        const string oldRecipes =
            "[{\"Id\":\"same\",\"Index\":1,\"Enabled\":true,\"Outputs\":{\"A\":{\"Quantity\":2,\"Name\":\"item\"}}}]";
        const string newRecipes =
            "[ { \"Outputs\": { \"A\": { \"Name\": \"item\", \"Quantity\": 2.0 } }, \"Enabled\": true, \"Index\": 1, \"Id\": \"same\" } ]";
        var oldSnapshot = await CreateSnapshotAsync("old", cubeRecipes: oldRecipes);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: newRecipes);

        var cube = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "cube-recipes");

        Assert.Equal(0, cube.Counts.Modified);
        Assert.Equal(1, cube.Counts.Unchanged);
    }

    [Fact]
    public async Task UniqueAddRemoveAndUnchangedUseStringIndexIdentity()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques:
            "[{\"Index\":\"Old Unique\",\"Code\":\"old\"},{\"Index\":\"Kept Unique\",\"Enabled\":true}]");
        var newSnapshot = await CreateSnapshotAsync("new", uniques:
            "[{\"Index\":\"New Unique\",\"Code\":\"new\"},{\"Index\":\"Kept Unique\",\"Enabled\":true}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var uniques = GetCategory(report, "uniques");

        Assert.Equal((1, 1, 0, 1),
            (uniques.Counts.Added, uniques.Counts.Removed, uniques.Counts.Modified, uniques.Counts.Unchanged));
        Assert.Equal("New Unique", Assert.Single(uniques.AddedRecords).Key);
        Assert.Equal("Old Unique", Assert.Single(uniques.RemovedRecords).Key);
        Assert.Equal("old", Assert.Single(uniques.RemovedRecords).Record.GetProperty("Code").GetString());
    }

    [Fact]
    public async Task UniquePropertyAndNumericRangeChangesIncludeFieldDiffAndBeforeAfterRecords()
    {
        const string oldUniques =
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strDamage\",\"args\":[10,20]}],\"Equipment\":{\"NormCode\":\"abc\"}}]";
        const string newUniques =
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strEnhancedDamage\",\"args\":[10,25]}],\"Equipment\":{\"NormCode\":\"def\"}}]";
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: oldUniques);
        var newSnapshot = await CreateSnapshotAsync("new", uniques: newUniques);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var uniques = GetCategory(report, "uniques");
        var modified = Assert.Single(uniques.ModifiedRecords);

        Assert.Equal(1, uniques.Counts.Modified);
        Assert.Contains(modified.Changes, static change => change.Path == "Lines[0].key");
        var rangeChange = Assert.Single(modified.Changes, static change => change.Path == "Lines[0].args[1]");
        Assert.Equal(20, rangeChange.OldValue!.Value.GetInt32());
        Assert.Equal(25, rangeChange.NewValue!.Value.GetInt32());
        Assert.Equal("abc", modified.Old.GetProperty("Equipment").GetProperty("NormCode").GetString());
        Assert.Equal("def", modified.New.GetProperty("Equipment").GetProperty("NormCode").GetString());
    }

    [Fact]
    public async Task UniquePropertyLineReorderingIsReportedAsAChange()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques:
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strFirst\"},{\"key\":\"strSecond\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new", uniques:
            "[{\"Index\":\"Unique\",\"Lines\":[{\"key\":\"strSecond\"},{\"key\":\"strFirst\"}]}]");

        var uniques = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "uniques");

        Assert.Equal(1, uniques.Counts.Modified);
        Assert.Contains(Assert.Single(uniques.ModifiedRecords).ChangedFields, static path => path == "Lines[0].key");
    }

    [Fact]
    public async Task DuplicateUniqueIdentityInvalidatesOnlyUniqueCategory()
    {
        var oldSnapshot =
            await CreateSnapshotAsync("old", uniques: "[{\"Index\":\"Duplicate\"},{\"Index\":\"Duplicate\"}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors,
            static issue => issue.Code == "duplicate-record-identity");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
    }

    [Fact]
    public async Task MissingUniqueIdentityInvalidatesCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "[{\"Code\":\"no-index\"}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors, static issue => issue.Code == "missing-record-identity");
    }

    [Fact]
    public async Task DuplicateSetIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets:
            "[{\"Index\":\"Duplicate\",\"SetItems\":[]},{\"Index\":\"Duplicate\",\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "duplicate-record-identity");
    }

    [Fact]
    public async Task MissingSetIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets: "[{\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "missing-record-identity");
    }

    [Fact]
    public async Task AddedAndRemovedSetsUseStringIndexIdentity()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets:
            "[{\"Index\":\"Removed Set\",\"SetItems\":[]},{\"Index\":\"Kept Set\",\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new", sets:
            "[{\"Index\":\"Added Set\",\"SetItems\":[]},{\"Index\":\"Kept Set\",\"SetItems\":[]}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var sets = GetCategory(report, "sets");

        Assert.Equal((1, 1, 0, 1),
            (sets.Counts.Added, sets.Counts.Removed, sets.Counts.Modified, sets.Counts.Unchanged));
        Assert.Equal("Added Set", Assert.Single(sets.AddedRecords).Key);
        Assert.Equal("Removed Set", Assert.Single(sets.RemovedRecords).Key);
        Assert.Equal("Index", sets.NestedMatchKey);
        Assert.Equal("SetItems", sets.NestedField);
    }

    [Fact]
    public async Task SetPieceChangesAndSharedBonusesAreReportedByPieceIdentity()
    {
        const string oldSets =
            "[{\"Index\":\"Set\",\"PartialBonuses\":[{\"key\":\"strPartial\",\"args\":[2]}],\"FullBonuses\":[{\"key\":\"strFull\",\"args\":[5]}],\"SetItems\":[{\"Index\":\"Hat\",\"Lines\":[{\"key\":\"strPiece\",\"args\":[1,10]}],\"SetBonuses\":[[{\"key\":\"strPieceBonus\",\"args\":[10]}]]},{\"Index\":\"Removed Piece\",\"Lines\":[]}]}]";
        const string newSets =
            "[{\"Index\":\"Set\",\"PartialBonuses\":[{\"key\":\"strPartial\",\"args\":[3]}],\"FullBonuses\":[{\"key\":\"strFull\",\"args\":[6]}],\"SetItems\":[{\"Index\":\"Hat\",\"Lines\":[{\"key\":\"strPiece\",\"args\":[1,15]}],\"SetBonuses\":[[{\"key\":\"strPieceBonus\",\"args\":[15]}]]},{\"Index\":\"Added Piece\",\"Lines\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", sets: oldSets);
        var newSnapshot = await CreateSnapshotAsync("new", sets: newSets);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var sets = GetCategory(report, "sets");
        var modified = Assert.Single(sets.ModifiedRecords);
        var nestedChanges = Assert.IsType<NestedRecordChanges>(modified.NestedChanges);

        Assert.Equal(1, sets.Counts.Modified);
        Assert.Equal((1, 1, 1), (sets.NestedCounts!.Added, sets.NestedCounts.Removed, sets.NestedCounts.Modified));
        Assert.Equal("Added Piece", Assert.Single(nestedChanges.Added).Key);
        Assert.Equal("Removed Piece", Assert.Single(nestedChanges.Removed).Key);
        Assert.Contains(Assert.Single(nestedChanges.Modified).ChangedFields, static path => path == "Lines[0].args[1]");
        Assert.Contains(modified.ChangedFields, static path => path == "PartialBonuses[0].args[0]");
        Assert.Contains(modified.ChangedFields, static path => path == "FullBonuses[0].args[0]");
        Assert.Contains(Assert.Single(nestedChanges.Modified).ChangedFields,
            static path => path == "SetBonuses[0][0].args[0]");
        Assert.Contains(modified.Changes,
            static change => change.Path == "SetItems[Index=\"Added Piece\"]" && change.Kind == "Added");
    }

    [Fact]
    public async Task ReorderingSetPiecesDoesNotCreateGameplayChanges()
    {
        const string oldSets =
            "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Hat\",\"Lines\":[]},{\"Index\":\"Boots\",\"Lines\":[]}]}]";
        const string newSets =
            "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Boots\",\"Lines\":[]},{\"Index\":\"Hat\",\"Lines\":[]}]}]";
        var oldSnapshot = await CreateSnapshotAsync("old", sets: oldSets);
        var newSnapshot = await CreateSnapshotAsync("new", sets: newSets);

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal(0, sets.Counts.Modified);
        Assert.Equal(1, sets.Counts.Unchanged);
        Assert.Equal(2, sets.NestedCounts!.Unchanged);
    }

    [Fact]
    public async Task DuplicateSetPieceIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", sets:
            "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Piece\"},{\"Index\":\"Piece\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var sets = GetCategory(report, "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "duplicate-nested-record-identity");
    }

    [Fact]
    public async Task MissingSetPieceIdentityInvalidatesSetsCategory()
    {
        var oldSnapshot =
            await CreateSnapshotAsync("old", sets: "[{\"Index\":\"Set\",\"SetItems\":[{\"Code\":\"no-index\"}]}]");
        var newSnapshot = await CreateSnapshotAsync("new");

        var sets = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "sets");

        Assert.Equal("NotCompared", sets.Status);
        Assert.Contains(sets.Errors, static issue => issue.Code == "missing-nested-record-identity");
    }

    [Fact]
    public async Task MissingCategoryFileFromManifestIsIsolatedFromOtherCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", omitFromManifest: [UniquePath]);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors, static issue => issue.Code == "file-not-in-manifest");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
        Assert.Empty(report.OldSnapshot.Errors);
    }

    [Fact]
    public async Task InvalidCategoryJsonDoesNotSuppressOtherCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "{");
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "uniques").Status);
        Assert.Contains(GetCategory(report, "uniques").Errors, static issue => issue.Code == "invalid-category-json");
        Assert.Equal("old", Assert.Single(GetCategory(report, "uniques").Errors).Snapshot);
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "sets").Status);
    }

    [Fact]
    public async Task HashMismatchIsCategorySpecific()
    {
        var oldSnapshot = await CreateSnapshotAsync("old");
        var newSnapshot = await CreateSnapshotAsync("new", hashOverrides: new Dictionary<string, string>
        {
            [SetPath] = new string('0', 64)
        });

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal("NotCompared", GetCategory(report, "sets").Status);
        Assert.Contains(GetCategory(report, "sets").Errors,
            static issue => issue.Code == "snapshot-file-hash-mismatch");
        Assert.Equal("Compared", GetCategory(report, "cube-recipes").Status);
        Assert.Equal("Compared", GetCategory(report, "uniques").Status);
    }

    [Fact]
    public async Task MissingManifestPreventsAllCategoriesFromBeingCompared()
    {
        var oldSnapshot = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.All(report.Categories, category => Assert.Equal("NotCompared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Errors, static issue => issue.Code == "manifest-unreadable"));
    }

    [Fact]
    public async Task FailedSnapshotPreventsAllCategoriesFromBeingCompared()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", succeeded: false);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.All(report.Categories, category => Assert.Equal("NotCompared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Errors, static issue => issue.Code == "snapshot-failed"));
    }

    [Fact]
    public async Task IncompleteSnapshotWarnsButVerifiedCategoryFilesCanBeCompared()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", complete: false);
        var newSnapshot = await CreateSnapshotAsync("new");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.True(report.Succeeded);
        Assert.All(report.Categories, category => Assert.Equal("Compared", category.Status));
        Assert.All(report.Categories,
            category => Assert.Contains(category.Warnings, static issue => issue.Code == "snapshot-incomplete"));
    }

    [Fact]
    public async Task SkippedCubeOutputIsNotTreatedAsDeletedRecipes()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", cubeRecipes: "[]", stepStatus: "Skipped", complete: false);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: "[{\"Id\":\"current\"}]");

        var cube = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "cube-recipes");

        Assert.Equal("NotCompared", cube.Status);
        Assert.Contains(cube.Errors, static issue => issue.Code == "export-step-not-written");
        Assert.Empty(cube.RemovedRecords);
    }

    [Fact]
    public async Task DuplicateCubeIdsRemainInvalid()
    {
        var duplicate = "[{\"Id\":\"same\"},{\"Id\":\"same\"}]";
        var oldSnapshot = await CreateSnapshotAsync("old", cubeRecipes: duplicate);
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: duplicate);

        var cube = GetCategory(await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot), "cube-recipes");

        Assert.Equal("NotCompared", cube.Status);
        Assert.Contains(cube.Errors, static issue => issue.Code == "duplicate-record-identity");
    }

    [Fact]
    public async Task IdenticalSnapshotsProduceNoChangesAndDeterministicReports()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            cubeRecipes: "[{\"Id\":\"cr\",\"Index\":0,\"Enabled\":true}]",
            uniques: "[{\"Index\":\"Unique\",\"Lines\":[]}]",
            sets: "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Piece\",\"Lines\":[]}]}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            cubeRecipes: "[{\"Id\":\"cr\",\"Index\":0,\"Enabled\":true}]",
            uniques: "[{\"Index\":\"Unique\",\"Lines\":[]}]",
            sets: "[{\"Index\":\"Set\",\"SetItems\":[{\"Index\":\"Piece\",\"Lines\":[]}]}]");
        var first = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var second = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var firstOutput = Path.Combine(_root, "comparison-one");
        var secondOutput = Path.Combine(_root, "comparison-two");

        await SnapshotComparison.WriteAsync(first, firstOutput);
        await SnapshotComparison.WriteAsync(second, secondOutput);

        Assert.True(first.Succeeded);
        Assert.Equal(2, first.SchemaVersion);
        Assert.Equal(0, first.Counts.Added + first.Counts.Removed + first.Counts.Modified);
        Assert.Equal(3, first.Counts.Unchanged);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(firstOutput, "changes.json")),
            await File.ReadAllBytesAsync(Path.Combine(secondOutput, "changes.json")));
    }

    [Fact]
    public async Task AggregateCountsSumComparedCategories()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "[{\"Index\":\"Unique\",\"Enabled\":false}]",
            sets: "[{\"Index\":\"Set\",\"SetItems\":[]}]");
        var newSnapshot = await CreateSnapshotAsync("new", cubeRecipes: "[{\"Id\":\"Recipe\"}]",
            uniques: "[{\"Index\":\"Unique\",\"Enabled\":true}]", sets: "[{\"Index\":\"Set\",\"SetItems\":[]}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal((1, 0, 1, 1),
            (report.Counts.Added, report.Counts.Removed, report.Counts.Modified, report.Counts.Unchanged));
    }

    [Fact]
    public async Task WritingComparisonDoesNotModifyInputSnapshots()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", uniques: "[{\"Index\":\"Old\"}]");
        var newSnapshot = await CreateSnapshotAsync("new", uniques: "[{\"Index\":\"New\"}]");
        var before = new[] { oldSnapshot, newSnapshot }
            .SelectMany(snapshot => CategoryFiles.Prepend("manifest.json")
                .Select(path => (Path: Path.Combine(snapshot, path),
                    Bytes: File.ReadAllBytes(Path.Combine(snapshot, path)))))
            .ToList();
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var outputDirectory = Path.Combine(_root, "comparison");

        await SnapshotComparison.WriteAsync(report, outputDirectory);

        Assert.True(File.Exists(Path.Combine(outputDirectory, "changes.json")));
        foreach (var file in before) Assert.Equal(file.Bytes, await File.ReadAllBytesAsync(file.Path));
    }

    [Fact]
    public void OutputDirectoryInsideAnInputSnapshotIsRefused()
    {
        var oldSnapshot = Path.Combine(_root, "old");
        var newSnapshot = Path.Combine(_root, "new");

        Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, oldSnapshot));
        Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot,
            Path.Combine(newSnapshot, "reports")));
        Assert.Null(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, Path.Combine(_root, "comparison")));
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string cubeRecipes = "[]",
        string uniques = "[]",
        string sets = "[]",
        string stepStatus = "Written",
        bool succeeded = true,
        bool complete = true,
        IReadOnlyCollection<string>? omitFromManifest = null,
        IReadOnlyDictionary<string, string>? hashOverrides = null)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CubeRecipePath] = cubeRecipes,
            [UniquePath] = uniques,
            [SetPath] = sets
        };
        var files = new List<object>();
        foreach (var (relativePath, content) in payloads)
        {
            var path = Path.Combine(snapshot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(path, bytes);
            if (stepStatus != "Written" ||
                omitFromManifest?.Contains(relativePath, StringComparer.Ordinal) == true) continue;
            var hash = hashOverrides?.GetValueOrDefault(relativePath) ??
                       Convert.ToHexStringLower(SHA256.HashData(bytes));
            files.Add(new { Path = relativePath, Bytes = bytes.LongLength, Sha256 = hash, Producer = "keyed-core" });
        }

        var outputs = stepStatus == "Written" ? CategoryFiles : [];
        var notWritten = stepStatus == "Written" ? Array.Empty<string>() : CategoryFiles;
        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = succeeded,
            Complete = complete,
            Steps = new[]
            {
                new { Producer = "keyed-core", Status = stepStatus, Outputs = outputs, NotWritten = notWritten }
            },
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static CategoryComparison GetCategory(ComparisonReport report, string category) =>
        Assert.Single(report.Categories, result => result.Category == category);
}
