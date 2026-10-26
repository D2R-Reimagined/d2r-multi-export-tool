// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Validates and compares existing export snapshots without modifying them.</summary>
public static class SnapshotComparison
{
    private const string CubeRecipeFile = "keyed/cube-recipes.json";

    /// <summary>Creates a structured comparison report for the cube recipes in two export snapshots.</summary>
    public static async Task<ComparisonReport> CompareAsync(string oldDirectory, string newDirectory)
    {
        var oldSnapshot = await SnapshotReader.LoadAsync(oldDirectory);
        var newSnapshot = await SnapshotReader.LoadAsync(newDirectory);
        var oldPayload = await oldSnapshot.ReadFileAsync(CubeRecipeFile);
        var newPayload = await newSnapshot.ReadFileAsync(CubeRecipeFile);
        CategoryComparison category;

        if (oldPayload is null || newPayload is null)
        {
            category = NotCompared(
                [.. oldSnapshot.Info.Errors, .. newSnapshot.Info.Errors],
                [.. oldSnapshot.Info.Warnings, .. newSnapshot.Info.Warnings],
                oldPayload?.Sha256,
                newPayload?.Sha256);
        }
        else
        {
            category = ComparePayloads(oldPayload, newPayload);
            category = CopySnapshotWarnings(category, oldSnapshot.Info.Warnings, newSnapshot.Info.Warnings);
        }

        var succeeded = oldSnapshot.Info.Errors.Count == 0
                        && newSnapshot.Info.Errors.Count == 0
                        && category.Errors.Count == 0;

        return new ComparisonReport
        {
            Succeeded = succeeded,
            OldSnapshot = oldSnapshot.Info,
            NewSnapshot = newSnapshot.Info,
            Categories = [category],
            Counts = category.Status == "Compared" ? category.Counts : new ComparisonCounts()
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

    private static CategoryComparison ComparePayloads(SnapshotPayload oldPayload, SnapshotPayload newPayload)
    {
        try
        {
            using var oldDocument = JsonDocument.Parse(oldPayload.Bytes);
            using var newDocument = JsonDocument.Parse(newPayload.Bytes);
            return CubeRecipeComparer.Compare(oldDocument.RootElement, newDocument.RootElement, oldPayload.Sha256,
                newPayload.Sha256);
        }
        catch (JsonException ex)
        {
            return NotCompared(
                [
                    new ComparisonIssue
                    {
                        Code = "invalid-cube-recipes-json", Message = $"Cannot parse '{CubeRecipeFile}': {ex.Message}"
                    }
                ],
                [],
                oldPayload.Sha256,
                newPayload.Sha256);
        }
    }

    private static CategoryComparison CopySnapshotWarnings(
        CategoryComparison category,
        IReadOnlyCollection<ComparisonIssue> oldWarnings,
        IReadOnlyCollection<ComparisonIssue> newWarnings)
    {
        if (oldWarnings.Count == 0 && newWarnings.Count == 0) return category;
        return new CategoryComparison
        {
            Category = category.Category,
            File = category.File,
            Status = category.Status,
            MatchKey = category.MatchKey,
            IgnoredFields = category.IgnoredFields,
            OldFileSha256 = category.OldFileSha256,
            NewFileSha256 = category.NewFileSha256,
            Errors = category.Errors,
            Warnings = [.. category.Warnings, .. oldWarnings, .. newWarnings],
            Counts = category.Counts,
            AddedRecipes = category.AddedRecipes,
            RemovedRecipes = category.RemovedRecipes,
            ModifiedRecipes = category.ModifiedRecipes
        };
    }

    private static CategoryComparison NotCompared(
        List<ComparisonIssue> errors,
        List<ComparisonIssue> warnings,
        string? oldHash,
        string? newHash) => new()
    {
        Category = "cube-recipes",
        File = CubeRecipeFile,
        Status = "NotCompared",
        MatchKey = "Id",
        IgnoredFields = ["Index"],
        OldFileSha256 = oldHash,
        NewFileSha256 = newHash,
        Errors = errors,
        Warnings = warnings
    };

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
