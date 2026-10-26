// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Versioned result of comparing two export snapshots.</summary>
public sealed class ComparisonReport
{
    /// <summary>Version of this comparison report contract.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>True when the comparison completed without validation or comparison errors.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Identity and validation details for the older snapshot.</summary>
    public SnapshotInfo OldSnapshot { get; init; } = new();

    /// <summary>Identity and validation details for the newer snapshot.</summary>
    public SnapshotInfo NewSnapshot { get; init; } = new();

    /// <summary>Counts across the categories that were compared.</summary>
    public ComparisonCounts Counts { get; init; } = new();

    /// <summary>Category-level comparison results.</summary>
    public List<CategoryComparison> Categories { get; init; } = [];
}

/// <summary>Snapshot path, manifest identity, and validation diagnostics.</summary>
public sealed class SnapshotInfo
{
    /// <summary>Absolute directory path supplied for this snapshot.</summary>
    public string Path { get; init; } = "";

    /// <summary>SHA-256 of the manifest bytes, when the manifest was readable.</summary>
    public string? ManifestSha256 { get; init; }

    /// <summary>Manifest schema version, when present and valid.</summary>
    public int? ManifestSchemaVersion { get; init; }

    /// <summary>Whether the export run recorded success.</summary>
    public bool? Succeeded { get; init; }

    /// <summary>Whether all export steps completed and wrote their declared outputs.</summary>
    public bool? Complete { get; init; }

    /// <summary>Validation errors that prevent safe comparison.</summary>
    public List<ComparisonIssue> Errors { get; init; } = [];

    /// <summary>Validation warnings that do not necessarily prevent comparison.</summary>
    public List<ComparisonIssue> Warnings { get; init; } = [];
}

/// <summary>One machine-readable validation or comparison diagnostic.</summary>
public sealed class ComparisonIssue
{
    /// <summary>Stable diagnostic code.</summary>
    public string Code { get; init; } = "";

    /// <summary>Human-readable diagnostic detail.</summary>
    public string Message { get; init; } = "";
}

/// <summary>Comparison outcome and recipe changes for one export category.</summary>
public sealed class CategoryComparison
{
    /// <summary>Stable category identifier.</summary>
    public string Category { get; init; } = "";

    /// <summary>Root-relative export file used by this category.</summary>
    public string File { get; init; } = "";

    /// <summary>Either <c>Compared</c> or <c>NotCompared</c>.</summary>
    public string Status { get; init; } = "NotCompared";

    /// <summary>Field used to associate records between snapshots.</summary>
    public string MatchKey { get; init; } = "";

    /// <summary>Fields excluded from gameplay change detection.</summary>
    public List<string> IgnoredFields { get; init; } = [];

    /// <summary>Verified SHA-256 of the old category file, if available.</summary>
    public string? OldFileSha256 { get; init; }

    /// <summary>Verified SHA-256 of the new category file, if available.</summary>
    public string? NewFileSha256 { get; init; }

    /// <summary>Category-specific validation or comparison errors.</summary>
    public List<ComparisonIssue> Errors { get; init; } = [];

    /// <summary>Category-specific warnings.</summary>
    public List<ComparisonIssue> Warnings { get; init; } = [];

    /// <summary>Counts for added, removed, modified, unchanged, and reindexed recipes.</summary>
    public ComparisonCounts Counts { get; init; } = new();

    /// <summary>Recipes present only in the new snapshot, including their full source JSON.</summary>
    public List<RecipeEntry> AddedRecipes { get; init; } = [];

    /// <summary>Recipes present only in the old snapshot, including their full source JSON.</summary>
    public List<RecipeEntry> RemovedRecipes { get; init; } = [];

    /// <summary>Recipes with changed gameplay data, including full before and after JSON.</summary>
    public List<ModifiedRecipe> ModifiedRecipes { get; init; } = [];
}

/// <summary>Counts of changes found in a snapshot comparison.</summary>
public sealed class ComparisonCounts
{
    /// <summary>Number of added records.</summary>
    public int Added { get; init; }

    /// <summary>Number of removed records.</summary>
    public int Removed { get; init; }

    /// <summary>Number of records with changed non-index data.</summary>
    public int Modified { get; init; }

    /// <summary>Number of matched records with identical non-index data.</summary>
    public int Unchanged { get; init; }

    /// <summary>Number of matched records whose row index changed, regardless of other changes.</summary>
    public int Reindexed { get; init; }
}

/// <summary>A complete recipe record included in the comparison result.</summary>
public sealed class RecipeEntry
{
    /// <summary>Stable recipe identity.</summary>
    public string Id { get; init; } = "";

    /// <summary>Source row index, when present and valid.</summary>
    public int? Index { get; init; }

    /// <summary>Complete original recipe object from the export.</summary>
    public JsonElement Recipe { get; init; }
}

/// <summary>A matched recipe whose gameplay data differs between snapshots.</summary>
public sealed class ModifiedRecipe
{
    /// <summary>Stable recipe identity shared by both snapshots.</summary>
    public string Id { get; init; } = "";

    /// <summary>Old source row index, when present and valid.</summary>
    public int? OldIndex { get; init; }

    /// <summary>New source row index, when present and valid.</summary>
    public int? NewIndex { get; init; }

    /// <summary>Paths of all changed fields, in deterministic order.</summary>
    public List<string> ChangedFields { get; init; } = [];

    /// <summary>Old and new values for every changed field path.</summary>
    public List<FieldChange> Changes { get; init; } = [];

    /// <summary>Complete original recipe object from the old export.</summary>
    public JsonElement Old { get; init; }

    /// <summary>Complete original recipe object from the new export.</summary>
    public JsonElement New { get; init; }
}

/// <summary>One structural JSON value change at a recipe field path.</summary>
public sealed class FieldChange
{
    /// <summary>Dot and array-index path to the changed value.</summary>
    public string Path { get; init; } = "";

    /// <summary>One of <c>Added</c>, <c>Removed</c>, or <c>Changed</c>.</summary>
    public string Kind { get; init; } = "Changed";

    /// <summary>Old value; null when the field did not exist in the old recipe.</summary>
    public JsonElement? OldValue { get; init; }

    /// <summary>New value; null when the field does not exist in the new recipe.</summary>
    public JsonElement? NewValue { get; init; }
}
