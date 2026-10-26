// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Compares keyed cube-recipe arrays by stable recipe id.</summary>
internal static class CubeRecipeComparer
{
    private static readonly HashSet<string> IgnoredRootFields = new(StringComparer.Ordinal)
    {
        "Index"
    };

    public static CategoryComparison Compare(JsonElement oldRecipes, JsonElement newRecipes, string? oldHash,
        string? newHash)
    {
        var errors = new List<ComparisonIssue>();
        var warnings = new List<ComparisonIssue>();
        if (oldRecipes.ValueKind != JsonValueKind.Array || newRecipes.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ComparisonIssue
            {
                Code = "invalid-cube-recipes",
                Message = "keyed/cube-recipes.json must contain a JSON array in both snapshots."
            });
            return NotCompared(errors, warnings, oldHash, newHash);
        }

        var oldById = ReadRecipes(oldRecipes, "old", errors, warnings);
        var newById = ReadRecipes(newRecipes, "new", errors, warnings);
        if (errors.Count > 0) return NotCompared(errors, warnings, oldHash, newHash);

        var added = newById.Values.Where(recipe => !oldById.ContainsKey(recipe.Id))
            .Select(ToRecipeEntry).OrderBy(static recipe => recipe.Index)
            .ThenBy(static recipe => recipe.Id, StringComparer.Ordinal).ToList();
        var removed = oldById.Values.Where(recipe => !newById.ContainsKey(recipe.Id))
            .Select(ToRecipeEntry).OrderBy(static recipe => recipe.Index)
            .ThenBy(static recipe => recipe.Id, StringComparer.Ordinal).ToList();
        var modified = new List<ModifiedRecipe>();
        var unchanged = 0;
        var reindexed = 0;

        foreach (var oldRecipe in oldById.Values.Where(recipe => newById.ContainsKey(recipe.Id))
                     .OrderBy(static recipe => recipe.Index).ThenBy(static recipe => recipe.Id, StringComparer.Ordinal))
        {
            var newRecipe = newById[oldRecipe.Id];
            if (oldRecipe.Index != newRecipe.Index) reindexed++;
            var changes = JsonStructuralDiff.Compare(oldRecipe.Value, newRecipe.Value, IgnoredRootFields);
            if (changes.Count == 0)
            {
                unchanged++;
                continue;
            }

            modified.Add(new ModifiedRecipe
            {
                Id = oldRecipe.Id,
                OldIndex = oldRecipe.Index,
                NewIndex = newRecipe.Index,
                ChangedFields = changes.Select(static change => change.Path).ToList(),
                Changes = changes,
                Old = oldRecipe.Value.Clone(),
                New = newRecipe.Value.Clone()
            });
        }

        var counts = new ComparisonCounts
        {
            Added = added.Count,
            Removed = removed.Count,
            Modified = modified.Count,
            Unchanged = unchanged,
            Reindexed = reindexed
        };

        return new CategoryComparison
        {
            Category = "cube-recipes",
            File = "keyed/cube-recipes.json",
            Status = "Compared",
            MatchKey = "Id",
            IgnoredFields = ["Index"],
            OldFileSha256 = oldHash,
            NewFileSha256 = newHash,
            Errors = errors,
            Warnings = warnings,
            Counts = counts,
            AddedRecipes = added,
            RemovedRecipes = removed,
            ModifiedRecipes = modified
        };
    }

    private static Dictionary<string, RecipeRecord> ReadRecipes(
        JsonElement recipes,
        string snapshotLabel,
        List<ComparisonIssue> errors,
        List<ComparisonIssue> warnings)
    {
        var result = new Dictionary<string, RecipeRecord>(StringComparer.Ordinal);
        var row = 0;
        foreach (var recipe in recipes.EnumerateArray())
        {
            row++;
            if (recipe.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ComparisonIssue
                {
                    Code = "invalid-recipe",
                    Message = $"The {snapshotLabel} cube recipe at array position {row - 1} is not a JSON object."
                });
                continue;
            }

            if (!recipe.TryGetProperty("Id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                                                                || string.IsNullOrWhiteSpace(idElement.GetString()))
            {
                errors.Add(new ComparisonIssue
                {
                    Code = "missing-recipe-id",
                    Message = $"The {snapshotLabel} cube recipe at array position {row - 1} has no non-empty string Id."
                });
                continue;
            }

            var id = idElement.GetString()!;
            if (result.ContainsKey(id))
            {
                errors.Add(new ComparisonIssue
                {
                    Code = "duplicate-recipe-id",
                    Message = $"The {snapshotLabel} cube recipe array contains duplicate Id '{id}'."
                });
                continue;
            }

            int? index = null;
            if (recipe.TryGetProperty("Index", out var indexElement))
            {
                if (indexElement.ValueKind == JsonValueKind.Number && indexElement.TryGetInt32(out var parsedIndex))
                    index = parsedIndex;
                else
                    warnings.Add(new ComparisonIssue
                    {
                        Code = "invalid-recipe-index",
                        Message =
                            $"The {snapshotLabel} cube recipe '{id}' has a missing or invalid integer Index; it is ignored for ordering and reindex counts."
                    });
            }
            else
                warnings.Add(new ComparisonIssue
                {
                    Code = "missing-recipe-index",
                    Message =
                        $"The {snapshotLabel} cube recipe '{id}' has no Index; it is ignored for ordering and reindex counts."
                });

            result.Add(id, new RecipeRecord(id, index, recipe.Clone()));
        }

        return result;
    }

    private static RecipeEntry ToRecipeEntry(RecipeRecord recipe) => new()
    {
        Id = recipe.Id,
        Index = recipe.Index,
        Recipe = recipe.Value.Clone()
    };

    private static CategoryComparison NotCompared(
        List<ComparisonIssue> errors,
        List<ComparisonIssue> warnings,
        string? oldHash,
        string? newHash) => new()
    {
        Category = "cube-recipes",
        File = "keyed/cube-recipes.json",
        Status = "NotCompared",
        MatchKey = "Id",
        IgnoredFields = ["Index"],
        OldFileSha256 = oldHash,
        NewFileSha256 = newHash,
        Errors = errors,
        Warnings = warnings
    };

    private sealed record RecipeRecord(string Id, int? Index, JsonElement Value);
}
