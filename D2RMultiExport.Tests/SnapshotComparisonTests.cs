// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Comparison;

namespace D2RMultiExport.Tests;

/// <summary>Manifest validation and cube-recipe snapshot comparison regressions.</summary>
public sealed class SnapshotComparisonTests : IDisposable
{
    private const string CubeRecipePath = "keyed/cube-recipes.json";
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-snapshot-comparison-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddedRecipeIncludesItsCompleteJson()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]");
        var newSnapshot =
            await CreateSnapshotAsync("new", "[{\"Id\":\"cr-new\",\"Index\":4,\"Description\":\"New recipe\"}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var category = Assert.Single(report.Categories);

        Assert.True(report.Succeeded);
        Assert.Equal(1, category.Counts.Added);
        Assert.Equal("cr-new", Assert.Single(category.AddedRecipes).Id);
        Assert.Equal("New recipe", category.AddedRecipes[0].Recipe.GetProperty("Description").GetString());
    }

    [Fact]
    public async Task RemovedRecipeIsReportedWithItsCompleteJson()
    {
        var recipe = "[{\"Id\":\"cr-old\",\"Index\":2,\"Description\":\"Old recipe\"}]";
        var oldSnapshot = await CreateSnapshotAsync("old", recipe);
        var newSnapshot = await CreateSnapshotAsync("new", "[]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var category = Assert.Single(report.Categories);

        Assert.Equal(1, category.Counts.Removed);
        Assert.Equal("Old recipe",
            Assert.Single(category.RemovedRecipes).Recipe.GetProperty("Description").GetString());
    }

    [Fact]
    public async Task ModifiedRecipeIncludesChangedPathsAndBeforeAfterValues()
    {
        var oldSnapshot = await CreateSnapshotAsync("old",
            "[{\"Id\":\"cr-same\",\"Index\":1,\"Outputs\":{\"A\":{\"Quantity\":1}}}]");
        var newSnapshot = await CreateSnapshotAsync("new",
            "[{\"Id\":\"cr-same\",\"Index\":1,\"Outputs\":{\"A\":{\"Quantity\":2}}}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var category = Assert.Single(report.Categories);
        var modified = Assert.Single(category.ModifiedRecipes);
        var change = Assert.Single(modified.Changes);

        Assert.Equal(1, category.Counts.Modified);
        Assert.Equal("Outputs.A.Quantity", change.Path);
        Assert.Equal(["Outputs.A.Quantity"], modified.ChangedFields);
        Assert.Equal(1, change.OldValue!.Value.GetInt32());
        Assert.Equal(2, change.NewValue!.Value.GetInt32());
        Assert.Equal(1, modified.Old.GetProperty("Outputs").GetProperty("A").GetProperty("Quantity").GetInt32());
        Assert.Equal(2, modified.New.GetProperty("Outputs").GetProperty("A").GetProperty("Quantity").GetInt32());
    }

    [Fact]
    public async Task UnchangedRecipeIsCountedAsUnchanged()
    {
        const string recipe = "[{\"Id\":\"cr-same\",\"Index\":1,\"Enabled\":true}]";
        var oldSnapshot = await CreateSnapshotAsync("old", recipe);
        var newSnapshot = await CreateSnapshotAsync("new", recipe);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var counts = Assert.Single(report.Categories).Counts;

        Assert.Equal(1, counts.Unchanged);
        Assert.Equal(0, counts.Modified);
    }

    [Fact]
    public async Task RowIndexShiftDoesNotCreateGameplayModification()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[{\"Id\":\"cr-same\",\"Index\":1,\"Enabled\":true}]");
        var newSnapshot = await CreateSnapshotAsync("new", "[{\"Id\":\"cr-same\",\"Index\":7,\"Enabled\":true}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var counts = Assert.Single(report.Categories).Counts;

        Assert.Equal(0, counts.Modified);
        Assert.Equal(1, counts.Unchanged);
        Assert.Equal(1, counts.Reindexed);
    }

    [Fact]
    public async Task FormattingAndObjectPropertyOrderDoNotCreateChanges()
    {
        const string oldRecipe =
            "[{\"Id\":\"cr-same\",\"Index\":1,\"Enabled\":true,\"Outputs\":{\"A\":{\"Quantity\":2,\"Name\":\"item\"}}}]";
        const string newRecipe =
            "[ { \"Outputs\": { \"A\": { \"Name\": \"item\", \"Quantity\": 2.0 } }, \"Enabled\": true, \"Index\": 1, \"Id\": \"cr-same\" } ]";
        var oldSnapshot = await CreateSnapshotAsync("old", oldRecipe);
        var newSnapshot = await CreateSnapshotAsync("new", newRecipe);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.Equal(0, Assert.Single(report.Categories).Counts.Modified);
    }

    [Fact]
    public async Task IdenticalSnapshotsProduceNoChanges()
    {
        const string recipes = "[{\"Id\":\"cr-1\",\"Index\":0,\"Enabled\":true}]";
        var oldSnapshot = await CreateSnapshotAsync("old", recipes);
        var newSnapshot = await CreateSnapshotAsync("new", recipes);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.Counts.Added);
        Assert.Equal(0, report.Counts.Removed);
        Assert.Equal(0, report.Counts.Modified);
        Assert.Equal(1, report.Counts.Unchanged);
        Assert.Equal(0, report.Counts.Reindexed);
    }

    [Fact]
    public async Task MissingManifestPreventsComparison()
    {
        var oldSnapshot = Directory.CreateDirectory(Path.Combine(_root, "old")).FullName;
        var newSnapshot = await CreateSnapshotAsync("new", "[]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var category = Assert.Single(report.Categories);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", category.Status);
        Assert.Contains(category.Errors, static error => error.Code == "manifest-unreadable");
        Assert.Empty(category.AddedRecipes);
        Assert.Empty(category.RemovedRecipes);
    }

    [Fact]
    public async Task StaleFileFromSkippedStepIsNotTreatedAsDeletedRecipes()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", stepStatus: "Skipped", complete: false);
        var newSnapshot = await CreateSnapshotAsync("new", "[{\"Id\":\"cr-new\"}]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var category = Assert.Single(report.Categories);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", category.Status);
        Assert.Contains(category.Errors, static error => error.Code == "export-step-not-written");
        Assert.Empty(category.RemovedRecipes);
    }

    [Fact]
    public async Task FileMissingFromManifestIsNotTrustedEvenWhenPresentOnDisk()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", includeFileInManifest: false);
        var newSnapshot = await CreateSnapshotAsync("new", "[]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var category = Assert.Single(report.Categories);

        Assert.Equal("NotCompared", category.Status);
        Assert.Contains(category.Errors, static error => error.Code == "file-not-in-manifest");
    }

    [Fact]
    public async Task FailedSnapshotIsNotCompared()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", succeeded: false, complete: false);
        var newSnapshot = await CreateSnapshotAsync("new", "[]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", Assert.Single(report.Categories).Status);
        Assert.Contains(report.OldSnapshot.Errors, static error => error.Code == "snapshot-failed");
    }

    [Fact]
    public async Task IncompleteSnapshotWarnsButAllowsVerifiedCategoryComparison()
    {
        const string recipes = "[{\"Id\":\"cr-same\",\"Index\":1}]";
        var oldSnapshot = await CreateSnapshotAsync("old", recipes, complete: false);
        var newSnapshot = await CreateSnapshotAsync("new", recipes);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.True(report.Succeeded);
        Assert.False(report.OldSnapshot.Complete);
        Assert.Equal("Compared", Assert.Single(report.Categories).Status);
        Assert.Contains(report.OldSnapshot.Warnings, static warning => warning.Code == "snapshot-incomplete");
    }

    [Fact]
    public async Task HashMismatchPreventsComparison()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]", hashOverride: new string('0', 64));
        var newSnapshot = await CreateSnapshotAsync("new", "[]");

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", Assert.Single(report.Categories).Status);
        Assert.Contains(report.OldSnapshot.Errors, static error => error.Code == "snapshot-file-hash-mismatch");
    }

    [Fact]
    public async Task DuplicateRecipeIdsMakeCategoryInvalid()
    {
        const string recipes = "[{\"Id\":\"duplicate\"},{\"Id\":\"duplicate\"}]";
        var oldSnapshot = await CreateSnapshotAsync("old", recipes);
        var newSnapshot = await CreateSnapshotAsync("new", recipes);

        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);

        Assert.False(report.Succeeded);
        Assert.Equal("NotCompared", Assert.Single(report.Categories).Status);
        Assert.Contains(Assert.Single(report.Categories).Errors, static error => error.Code == "duplicate-recipe-id");
    }

    [Fact]
    public void OutputInsideEitherInputSnapshotIsRefused()
    {
        var oldSnapshot = Path.Combine(_root, "old");
        var newSnapshot = Path.Combine(_root, "new");

        Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, oldSnapshot));
        Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot,
            Path.Combine(newSnapshot, "reports")));
        Assert.Null(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, Path.Combine(_root, "comparison")));
    }

    [Fact]
    public async Task OutputDirectoryLinkIntoInputSnapshotIsRefused()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]");
        var newSnapshot = await CreateSnapshotAsync("new", "[]");
        var linkedOutput = Path.Combine(_root, "comparison-link");
        Assert.True(await TryCreateDirectoryLinkAsync(linkedOutput, oldSnapshot));

        try
        {
            Assert.NotNull(SnapshotComparison.FindOutputConflict(oldSnapshot, newSnapshot, linkedOutput));
        }
        finally
        {
            Directory.Delete(linkedOutput);
        }
    }

    [Fact]
    public async Task WritingChangesJsonDoesNotModifyInputSnapshots()
    {
        var oldSnapshot = await CreateSnapshotAsync("old", "[]");
        var newSnapshot = await CreateSnapshotAsync("new", "[]");
        var oldBytes = await File.ReadAllBytesAsync(Path.Combine(oldSnapshot, "manifest.json"));
        var newBytes = await File.ReadAllBytesAsync(Path.Combine(newSnapshot, "keyed", "cube-recipes.json"));
        var report = await SnapshotComparison.CompareAsync(oldSnapshot, newSnapshot);
        var outputDirectory = Path.Combine(_root, "comparison");

        await SnapshotComparison.WriteAsync(report, outputDirectory);

        Assert.True(File.Exists(Path.Combine(outputDirectory, "changes.json")));
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Path.Combine(oldSnapshot, "manifest.json")));
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(Path.Combine(newSnapshot, "keyed", "cube-recipes.json")));
        using var changes =
            JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "changes.json")));
        Assert.Equal(1, changes.RootElement.GetProperty("SchemaVersion").GetInt32());
    }

    private async Task<string> CreateSnapshotAsync(
        string name,
        string recipes,
        string stepStatus = "Written",
        bool succeeded = true,
        bool complete = true,
        bool includeFileInManifest = true,
        string? hashOverride = null)
    {
        var snapshot = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        var cubeDirectory = Directory.CreateDirectory(Path.Combine(snapshot, "keyed")).FullName;
        var cubePath = Path.Combine(cubeDirectory, "cube-recipes.json");
        var bytes = Encoding.UTF8.GetBytes(recipes);
        await File.WriteAllBytesAsync(cubePath, bytes);
        var hash = hashOverride ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
        var outputs = stepStatus == "Written" ? new[] { CubeRecipePath } : Array.Empty<string>();
        var notWritten = stepStatus == "Written" ? Array.Empty<string>() : new[] { CubeRecipePath };
        var files = includeFileInManifest && stepStatus == "Written"
            ? new[] { new { Path = CubeRecipePath, Bytes = bytes.LongLength, Sha256 = hash, Producer = "keyed-core" } }
            : Array.Empty<object>();
        var steps = new[]
        {
            new { Producer = "keyed-core", Status = stepStatus, Outputs = outputs, NotWritten = notWritten }
        };
        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = succeeded,
            Complete = complete,
            Steps = steps,
            Files = files
        };
        await File.WriteAllTextAsync(Path.Combine(snapshot, "manifest.json"), JsonSerializer.Serialize(manifest));
        return snapshot;
    }

    private static async Task<bool> TryCreateDirectoryLinkAsync(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe",
                $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
    }
}
