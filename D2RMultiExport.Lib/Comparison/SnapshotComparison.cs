// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Validates and compares existing export snapshots without modifying them.</summary>
public static class SnapshotComparison
{
    /// <summary>Creates a structured comparison report for supported records in two export snapshots.</summary>
    public static async Task<ComparisonReport> CompareAsync(string oldDirectory, string newDirectory)
    {
        var oldSnapshot = await SnapshotReader.LoadAsync(oldDirectory);
        var newSnapshot = await SnapshotReader.LoadAsync(newDirectory);
        var categories = new List<CategoryComparison>(RecordComparer.Categories.Count);
        foreach (var definition in RecordComparer.Categories)
        {
            var oldFile = await oldSnapshot.ReadFileAsync(definition.File);
            var newFile = await newSnapshot.ReadFileAsync(definition.File);
            categories.Add(CompareCategory(definition, oldSnapshot.Info, newSnapshot.Info, oldFile, newFile));
        }

        var succeeded = oldSnapshot.Info.Errors.Count == 0
                        && newSnapshot.Info.Errors.Count == 0
                        && categories.All(static category => category.Errors.Count == 0);
        var comparedCategories = categories.Where(static category => category.Status == "Compared").ToList();

        return new ComparisonReport
        {
            Succeeded = succeeded,
            OldSnapshot = oldSnapshot.Info,
            NewSnapshot = newSnapshot.Info,
            Categories = categories,
            Counts = new ComparisonCounts
            {
                Added = comparedCategories.Sum(static category => category.Counts.Added),
                Removed = comparedCategories.Sum(static category => category.Counts.Removed),
                Modified = comparedCategories.Sum(static category => category.Counts.Modified),
                Unchanged = comparedCategories.Sum(static category => category.Counts.Unchanged),
                Reindexed = comparedCategories.Sum(static category => category.Counts.Reindexed)
            }
        };
    }

    /// <summary>Returns a refusal reason when the comparison output equals or is inside either input snapshot.</summary>
    public static string? FindOutputConflict(string oldDirectory, string newDirectory, string outputDirectory)
    {
        var output = ResolveExistingLinks(Path.GetFullPath(outputDirectory));
        var oldRoot = ResolveExistingLinks(Path.GetFullPath(oldDirectory));
        var newRoot = ResolveExistingLinks(Path.GetFullPath(newDirectory));
        if (IsSameOrInside(output, oldRoot))
            return $"Comparison output '{output}' must not be the old snapshot directory or a directory inside it.";
        if (IsSameOrInside(output, newRoot))
            return $"Comparison output '{output}' must not be the new snapshot directory or a directory inside it.";
        return null;
    }

    /// <summary>Writes the comparison report as changes.json through the atomic output gateway.</summary>
    public static Task WriteAsync(ComparisonReport report, string outputDirectory, bool prettyPrint = true)
    {
        var outputs = new ExportOutputs(outputDirectory) { Producer = "snapshot-comparison" };
        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return outputs.WriteJsonAsync("changes.json", report, options);
    }

    /// <summary>Renders the cube-recipe changes from an existing report as human-readable Markdown.</summary>
    public static Task<string> RenderRecipeChangesAsync(ComparisonReport report) =>
        RecipeChangeMarkdown.RenderAsync(report);

    /// <summary>Writes the recipe Markdown report through the atomic output gateway.</summary>
    public static Task WriteRecipeChangesAsync(string markdown, string outputDirectory)
    {
        var outputs = new ExportOutputs(outputDirectory) { Producer = "snapshot-comparison" };
        return outputs.WriteTextAsync("recipe-changes.md", markdown);
    }

    private static CategoryComparison CompareCategory(
        RecordComparer.ComparisonDefinition definition,
        SnapshotInfo oldSnapshot,
        SnapshotInfo newSnapshot,
        SnapshotFileResult oldFile,
        SnapshotFileResult newFile)
    {
        CategoryComparison category;
        if (oldFile.Payload is null || newFile.Payload is null)
        {
            category = NotCompared(
                definition,
                [
                    .. WithSnapshot(oldSnapshot.Errors, "old"), .. WithSnapshot(newSnapshot.Errors, "new"),
                    .. WithSnapshot(oldFile.Errors, "old"), .. WithSnapshot(newFile.Errors, "new")
                ],
                [],
                oldFile.Payload?.Sha256,
                newFile.Payload?.Sha256);
        }
        else
            category = ComparePayloads(definition, oldFile.Payload, newFile.Payload);

        return CopySnapshotDiagnostics(category, oldSnapshot, newSnapshot, oldFile, newFile);
    }

    private static CategoryComparison ComparePayloads(
        RecordComparer.ComparisonDefinition definition,
        SnapshotPayload oldPayload,
        SnapshotPayload newPayload)
    {
        JsonDocument oldDocument;
        try
        {
            oldDocument = JsonDocument.Parse(oldPayload.Bytes);
        }
        catch (JsonException ex)
        {
            return NotCompared(
                definition,
                [
                    new ComparisonIssue
                    {
                        Code = "invalid-category-json",
                        Message = $"Cannot parse old '{definition.File}': {ex.Message}",
                        Snapshot = "old"
                    }
                ],
                [],
                oldPayload.Sha256,
                newPayload.Sha256);
        }

        using (oldDocument)
        {
            JsonDocument newDocument;
            try
            {
                newDocument = JsonDocument.Parse(newPayload.Bytes);
            }
            catch (JsonException ex)
            {
                return NotCompared(
                    definition,
                    [
                        new ComparisonIssue
                        {
                            Code = "invalid-category-json",
                            Message = $"Cannot parse new '{definition.File}': {ex.Message}",
                            Snapshot = "new"
                        }
                    ],
                    [],
                    oldPayload.Sha256,
                    newPayload.Sha256);
            }

            using (newDocument)
                return RecordComparer.Compare(definition, oldDocument.RootElement, newDocument.RootElement,
                    oldPayload.Sha256, newPayload.Sha256);
        }
    }

    private static CategoryComparison CopySnapshotDiagnostics(
        CategoryComparison category,
        SnapshotInfo oldSnapshot,
        SnapshotInfo newSnapshot,
        SnapshotFileResult oldFile,
        SnapshotFileResult newFile)
    {
        return new CategoryComparison
        {
            Category = category.Category,
            File = category.File,
            Status = category.Status,
            MatchKey = category.MatchKey,
            VariantField = category.VariantField,
            VariantMatchKey = category.VariantMatchKey,
            IgnoredFields = category.IgnoredFields,
            OldFileSha256 = category.OldFileSha256,
            NewFileSha256 = category.NewFileSha256,
            Errors = category.Errors,
            Warnings =
            [
                .. category.Warnings, .. WithSnapshot(oldSnapshot.Warnings, "old"),
                .. WithSnapshot(newSnapshot.Warnings, "new"), .. WithSnapshot(oldFile.Warnings, "old"),
                .. WithSnapshot(newFile.Warnings, "new")
            ],
            Counts = category.Counts,
            NestedField = category.NestedField,
            NestedMatchKey = category.NestedMatchKey,
            NestedCounts = category.NestedCounts,
            AddedRecords = category.AddedRecords,
            RemovedRecords = category.RemovedRecords,
            ModifiedRecords = category.ModifiedRecords
        };
    }

    private static CategoryComparison NotCompared(
        RecordComparer.ComparisonDefinition definition,
        List<ComparisonIssue> errors,
        List<ComparisonIssue> warnings,
        string? oldHash,
        string? newHash) => new()
    {
        Category = definition.Category,
        File = definition.File,
        Status = "NotCompared",
        MatchKey = definition.MatchKey,
        VariantField = definition.VariantArrayField,
        VariantMatchKey = definition.VariantMatchKey,
        IgnoredFields = [.. definition.IgnoredRootFields],
        OldFileSha256 = oldHash,
        NewFileSha256 = newHash,
        Errors = errors,
        Warnings = warnings,
        NestedField = definition.NestedArrayField,
        NestedMatchKey = definition.NestedMatchKey
    };

    private static IEnumerable<ComparisonIssue> WithSnapshot(IEnumerable<ComparisonIssue> issues, string snapshot) =>
        issues.Select(issue => new ComparisonIssue
        {
            Code = issue.Code,
            Message = issue.Message,
            Snapshot = snapshot
        });

    private static bool IsSameOrInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return relative == "."
               || !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, comparison);
    }

    private static string ResolveExistingLinks(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath)
                      ?? throw new ArgumentException($"Path '{fullPath}' has no root.", nameof(fullPath));
        var segments = fullPath[current.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)
                             ?? throw new IOException($"Cannot resolve filesystem link '{candidate}'.");
                current = Path.GetFullPath(target.FullName);
            }
            else
                current = candidate;
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }
}
