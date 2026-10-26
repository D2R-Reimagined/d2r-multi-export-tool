// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Compares manifest-verified keyed export records by their declared identities.</summary>
internal static class RecordComparer
{
    /// <summary>Supported categories and their stable identity/nested-collection contracts.</summary>
    private static readonly ComparisonDefinition[] Definitions =
    [
        new("cube-recipes", "keyed/cube-recipes.json", "Id", "Index", ["Index"]),
        new("uniques", "keyed/uniques.json", "Index"),
        new("sets", "keyed/sets.json", "Index", NestedArrayField: "SetItems", NestedMatchKey: "Index"),
        new("runewords", "keyed/runewords.json", "Index", VariantArrayField: "Types", VariantMatchKey: "Index"),
        new("armors", "keyed/armors.json", "NameKey", NestedArrayField: "AutoMagicGroups",
            NestedMatchKey: "Code", NestedArrayOptional: true),
        new("weapons", "keyed/weapons.json", "NameKey", NestedArrayField: "AutoMagicGroups",
            NestedMatchKey: "Code", NestedArrayOptional: true)
    ];

    public static IReadOnlyList<ComparisonDefinition> Categories => Definitions;

    public static CategoryComparison Compare(
        ComparisonDefinition definition,
        JsonElement oldRecords,
        JsonElement newRecords,
        string? oldHash,
        string? newHash)
    {
        var errors = new List<ComparisonIssue>();
        var warnings = new List<ComparisonIssue>();
        if (oldRecords.ValueKind != JsonValueKind.Array || newRecords.ValueKind != JsonValueKind.Array)
        {
            errors.Add(new ComparisonIssue
            {
                Code = "invalid-category-data",
                Message = $"{definition.File} must contain a JSON array in both snapshots."
            });
            return NotCompared(definition, errors, warnings, oldHash, newHash);
        }

        Dictionary<string, RecordData> oldByKey;
        Dictionary<string, RecordData> newByKey;
        if (definition.VariantArrayField is { } variantArrayField && definition.VariantMatchKey is { } variantMatchKey)
        {
            var oldVariants =
                ReadVariantRecords(definition, oldRecords, variantArrayField, variantMatchKey, "old", errors);
            var newVariants =
                ReadVariantRecords(definition, newRecords, variantArrayField, variantMatchKey, "new", errors);
            if (errors.Count > 0) return NotCompared(definition, errors, warnings, oldHash, newHash);
            oldByKey = BuildVariantRecords(oldVariants, newVariants, "old", warnings);
            newByKey = BuildVariantRecords(newVariants, oldVariants, "new", warnings);
        }
        else
        {
            oldByKey = ReadRecords(definition, oldRecords, "old", errors, warnings);
            newByKey = ReadRecords(definition, newRecords, "new", errors, warnings);
        }

        if (errors.Count > 0) return NotCompared(definition, errors, warnings, oldHash, newHash);

        var added = newByKey.Values.Where(record => !oldByKey.ContainsKey(record.Key))
            .Select(ToRecordEntry)
            .OrderBy(record => definition.RowIndexField is null ? 0 : record.RowIndex ?? int.MaxValue)
            .ThenBy(static record => record.Key, StringComparer.Ordinal)
            .ToList();
        var removed = oldByKey.Values.Where(record => !newByKey.ContainsKey(record.Key))
            .Select(ToRecordEntry)
            .OrderBy(record => definition.RowIndexField is null ? 0 : record.RowIndex ?? int.MaxValue)
            .ThenBy(static record => record.Key, StringComparer.Ordinal)
            .ToList();
        var modified = new List<ModifiedRecord>();
        var nestedCounts = definition.NestedArrayField is null ? null : new ComparisonCountsBuilder();
        var ignoredRootFields = new HashSet<string>(definition.IgnoredRootFields, StringComparer.Ordinal);
        var unchanged = 0;
        var reindexed = 0;

        foreach (var oldRecord in oldByKey.Values
                     .Where(record => newByKey.ContainsKey(record.Key))
                     .OrderBy(record => definition.RowIndexField is null ? 0 : record.RowIndex ?? int.MaxValue)
                     .ThenBy(static record => record.Key, StringComparer.Ordinal))
        {
            var newRecord = newByKey[oldRecord.Key];
            if (oldRecord.RowIndex != newRecord.RowIndex && definition.RowIndexField is not null) reindexed++;

            var nestedChanges = definition.NestedArrayField is null
                ? null
                : CompareNestedRecords(definition, oldRecord, newRecord, nestedCounts!);
            var changes = JsonStructuralDiff.Compare(oldRecord.Value, newRecord.Value,
                ignoredRootFields, definition.NestedArrayField ?? definition.VariantArrayField,
                definition.NestedMatchKey ?? definition.VariantMatchKey,
                definition.NestedArrayOptional);
            if (changes.Count == 0)
            {
                unchanged++;
                continue;
            }

            modified.Add(new ModifiedRecord
            {
                Key = oldRecord.Key,
                OldRowIndex = oldRecord.RowIndex,
                NewRowIndex = newRecord.RowIndex,
                VariantTypes = oldRecord.VariantTypes,
                ChangedFields = changes.Select(static change => change.Path).ToList(),
                Changes = changes,
                NestedChanges = nestedChanges,
                Old = oldRecord.Value.Clone(),
                New = newRecord.Value.Clone()
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
            Category = definition.Category,
            File = definition.File,
            Status = "Compared",
            MatchKey = definition.MatchKey,
            VariantField = definition.VariantArrayField,
            VariantMatchKey = definition.VariantMatchKey,
            IgnoredFields = [.. definition.IgnoredRootFields],
            OldFileSha256 = oldHash,
            NewFileSha256 = newHash,
            Errors = errors,
            Warnings = warnings,
            Counts = counts,
            NestedField = definition.NestedArrayField,
            NestedMatchKey = definition.NestedMatchKey,
            NestedCounts = nestedCounts?.Build(),
            AddedRecords = added,
            RemovedRecords = removed,
            ModifiedRecords = modified
        };
    }

    private static Dictionary<string, RecordData> ReadRecords(
        ComparisonDefinition definition,
        JsonElement records,
        string snapshot,
        List<ComparisonIssue> errors,
        List<ComparisonIssue> warnings)
    {
        var result = new Dictionary<string, RecordData>(StringComparer.Ordinal);
        var position = 0;
        foreach (var record in records.EnumerateArray())
        {
            var currentPosition = position++;
            if (record.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Issue("invalid-record",
                    $"The {snapshot} {definition.Category} record at array position {currentPosition} is not a JSON object.",
                    snapshot));
                continue;
            }

            if (!TryReadIdentity(record, definition.MatchKey, out var key))
            {
                errors.Add(Issue("missing-record-identity",
                    $"The {snapshot} {definition.Category} record at array position {currentPosition} has no non-empty string {definition.MatchKey}.",
                    snapshot));
                continue;
            }

            if (result.ContainsKey(key))
            {
                errors.Add(Issue("duplicate-record-identity",
                    $"The {snapshot} {definition.Category} array contains duplicate {definition.MatchKey} '{key}'.",
                    snapshot));
                continue;
            }

            int? rowIndex = null;
            if (definition.RowIndexField is { } rowIndexField)
            {
                if (record.TryGetProperty(rowIndexField, out var indexValue) && indexValue.ValueKind ==
                                                                             JsonValueKind.Number
                                                                             && indexValue.TryGetInt32(
                                                                                 out var parsedIndex))
                    rowIndex = parsedIndex;
                else
                    warnings.Add(Issue("invalid-row-index",
                        $"The {snapshot} {definition.Category} record '{key}' has a missing or invalid integer {rowIndexField}; it is ignored for ordering and reindex counts.",
                        snapshot));
            }

            Dictionary<string, JsonElement>? nestedRecords = null;
            if (definition.NestedArrayField is { } nestedField && definition.NestedMatchKey is { } nestedMatchKey)
            {
                nestedRecords =
                    ReadNestedRecords(definition, record, key, nestedField, nestedMatchKey, snapshot, errors);
            }

            result.Add(key, new RecordData(key, rowIndex, record.Clone(), nestedRecords));
        }

        return result;
    }

    private static List<VariantRecord> ReadVariantRecords(
        ComparisonDefinition definition,
        JsonElement records,
        string variantArrayField,
        string variantMatchKey,
        string snapshot,
        List<ComparisonIssue> errors)
    {
        var result = new List<VariantRecord>();
        var position = 0;
        foreach (var record in records.EnumerateArray())
        {
            var currentPosition = position++;
            if (record.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Issue("invalid-record",
                    $"The {snapshot} {definition.Category} record at array position {currentPosition} is not a JSON object.",
                    snapshot));
                continue;
            }

            if (!TryReadIdentity(record, definition.MatchKey, out var key))
            {
                errors.Add(Issue("missing-record-identity",
                    $"The {snapshot} {definition.Category} record at array position {currentPosition} has no non-empty string {definition.MatchKey}.",
                    snapshot));
                continue;
            }

            if (!record.TryGetProperty(variantArrayField, out var variants) ||
                variants.ValueKind != JsonValueKind.Array)
            {
                errors.Add(Issue("invalid-variant-identity",
                    $"The {snapshot} {definition.Category} record '{key}' at array position {currentPosition} must contain an array '{variantArrayField}'.",
                    snapshot));
                continue;
            }

            var variantTypes = new List<string>();
            var seenTypes = new HashSet<string>(StringComparer.Ordinal);
            var invalidTypes = false;
            foreach (var variant in variants.EnumerateArray())
            {
                if (variant.ValueKind != JsonValueKind.Object ||
                    !TryReadIdentity(variant, variantMatchKey, out var typeKey))
                {
                    invalidTypes = true;
                    break;
                }

                if (!seenTypes.Add(typeKey))
                {
                    invalidTypes = true;
                    break;
                }

                variantTypes.Add(typeKey);
            }

            if (invalidTypes)
            {
                errors.Add(Issue("invalid-variant-identity",
                    $"The {snapshot} {definition.Category} record '{key}' at array position {currentPosition} contains a non-object, missing, empty, or duplicate '{variantMatchKey}' in '{variantArrayField}'.",
                    snapshot));
                continue;
            }

            variantTypes.Sort(StringComparer.Ordinal);
            result.Add(new VariantRecord(key, variantTypes, currentPosition, record.Clone()));
        }

        return result;
    }

    private static Dictionary<string, RecordData> BuildVariantRecords(
        List<VariantRecord> records,
        List<VariantRecord> oppositeRecords,
        string snapshot,
        List<ComparisonIssue> warnings)
    {
        var counts = records.GroupBy(static record => record.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var oppositeCounts = oppositeRecords.GroupBy(static record => record.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var ambiguousSignatures = new HashSet<string>(StringComparer.Ordinal);
        var signatures = records.Concat(oppositeRecords)
            .GroupBy(static record => CreateVariantKey(record.Name, record.Types), StringComparer.Ordinal);

        foreach (var signature in signatures)
        {
            var oldCount = records.Count(record => string.Equals(
                CreateVariantKey(record.Name, record.Types), signature.Key, StringComparison.Ordinal));
            var newCount = oppositeRecords.Count(record => string.Equals(
                CreateVariantKey(record.Name, record.Types), signature.Key, StringComparison.Ordinal));
            if (oldCount <= 1 && newCount <= 1) continue;

            ambiguousSignatures.Add(signature.Key);
            if (snapshot == "old")
            {
                warnings.Add(new ComparisonIssue
                {
                    Code = "ambiguous-variant-identity",
                    Message =
                        $"Runeword variant identity {signature.Key} occurs more than once in at least one snapshot; its records are reported as additions/removals rather than paired."
                });
            }
        }

        var result = new Dictionary<string, RecordData>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var isVariant = counts.GetValueOrDefault(record.Name) > 1 ||
                            oppositeCounts.GetValueOrDefault(record.Name) > 1;
            var key = isVariant ? CreateVariantKey(record.Name, record.Types) : record.Name;
            if (isVariant && ambiguousSignatures.Contains(key)) key = $"{key}#{snapshot}-{record.Position}";
            result.Add(key, new RecordData(key, null, record.Value, null, isVariant ? record.Types : null));
        }

        return result;
    }

    private static string CreateVariantKey(string name, IReadOnlyList<string> types) =>
        $"{JsonSerializer.Serialize(name)}[Types={JsonSerializer.Serialize(types)}]";

    private static Dictionary<string, JsonElement> ReadNestedRecords(
        ComparisonDefinition definition,
        JsonElement parent,
        string parentKey,
        string nestedField,
        string nestedMatchKey,
        string snapshot,
        List<ComparisonIssue> errors)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!parent.TryGetProperty(nestedField, out var nestedArray) || nestedArray.ValueKind == JsonValueKind.Null)
        {
            if (definition.NestedArrayOptional) return result;

            errors.Add(Issue("invalid-nested-records",
                $"The {snapshot} {definition.Category} record '{parentKey}' must contain an array '{nestedField}'.",
                snapshot));
            return result;
        }

        if (nestedArray.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Issue("invalid-nested-records",
                $"The {snapshot} {definition.Category} record '{parentKey}' must contain an array '{nestedField}'.",
                snapshot));
            return result;
        }

        var position = 0;
        foreach (var nestedRecord in nestedArray.EnumerateArray())
        {
            var currentPosition = position++;
            if (nestedRecord.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Issue("invalid-nested-record",
                    $"The {snapshot} {definition.Category} record '{parentKey}' has a non-object {nestedField} entry at position {currentPosition}.",
                    snapshot));
                continue;
            }

            if (!TryReadIdentity(nestedRecord, nestedMatchKey, out var nestedKey))
            {
                errors.Add(Issue("missing-nested-record-identity",
                    $"The {snapshot} {definition.Category} record '{parentKey}' has a {nestedField} entry without a non-empty string {nestedMatchKey}.",
                    snapshot));
                continue;
            }

            if (!result.TryAdd(nestedKey, nestedRecord.Clone()))
                errors.Add(Issue("duplicate-nested-record-identity",
                    $"The {snapshot} {definition.Category} record '{parentKey}' contains duplicate {nestedField} {nestedMatchKey} '{nestedKey}'.",
                    snapshot));
        }

        return result;
    }

    private static NestedRecordChanges? CompareNestedRecords(
        ComparisonDefinition definition,
        RecordData oldParent,
        RecordData newParent,
        ComparisonCountsBuilder counts)
    {
        var nestedMatchKey = definition.NestedMatchKey!;
        var oldRecords = oldParent.NestedRecords!;
        var newRecords = newParent.NestedRecords!;
        var ignoredFields = new HashSet<string>(StringComparer.Ordinal);
        var added = newRecords.Where(pair => !oldRecords.ContainsKey(pair.Key))
            .Select(pair => new RecordEntry { Key = pair.Key, Record = pair.Value.Clone() })
            .OrderBy(static record => record.Key, StringComparer.Ordinal).ToList();
        var removed = oldRecords.Where(pair => !newRecords.ContainsKey(pair.Key))
            .Select(pair => new RecordEntry { Key = pair.Key, Record = pair.Value.Clone() })
            .OrderBy(static record => record.Key, StringComparer.Ordinal).ToList();
        var modified = new List<ModifiedRecord>();
        var unchanged = 0;

        foreach (var oldPiece in oldRecords.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!newRecords.TryGetValue(oldPiece.Key, out var newPiece)) continue;
            var pieceChanges = JsonStructuralDiff.Compare(oldPiece.Value, newPiece, ignoredFields);
            if (pieceChanges.Count == 0)
            {
                unchanged++;
                continue;
            }

            modified.Add(new ModifiedRecord
            {
                Key = oldPiece.Key,
                ChangedFields = pieceChanges.Select(static change => change.Path).ToList(),
                Changes = pieceChanges,
                Old = oldPiece.Value.Clone(),
                New = newPiece.Clone()
            });
        }

        counts.Added += added.Count;
        counts.Removed += removed.Count;
        counts.Modified += modified.Count;
        counts.Unchanged += unchanged;

        if (added.Count == 0 && removed.Count == 0 && modified.Count == 0) return null;
        return new NestedRecordChanges
        {
            Field = definition.NestedArrayField!,
            MatchKey = nestedMatchKey,
            Added = added,
            Removed = removed,
            Modified = modified
        };
    }

    private static bool TryReadIdentity(JsonElement record, string field, out string key)
    {
        key = "";
        if (!record.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) return false;
        key = value.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(key);
    }

    private static RecordEntry ToRecordEntry(RecordData record) => new()
    {
        Key = record.Key,
        RowIndex = record.RowIndex,
        VariantTypes = record.VariantTypes,
        Record = record.Value.Clone()
    };

    private static CategoryComparison NotCompared(
        ComparisonDefinition definition,
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

    private static ComparisonIssue Issue(string code, string message, string snapshot) => new()
    {
        Code = code,
        Message = message,
        Snapshot = snapshot
    };

    internal sealed record ComparisonDefinition(
        string Category,
        string File,
        string MatchKey,
        string? RowIndexField = null,
        string[]? IgnoredRootFields = null,
        string? NestedArrayField = null,
        string? NestedMatchKey = null,
        string? VariantArrayField = null,
        string? VariantMatchKey = null,
        bool NestedArrayOptional = false)
    {
        public string[] IgnoredRootFields { get; init; } = IgnoredRootFields ?? [];
    }

    private sealed record RecordData(
        string Key,
        int? RowIndex,
        JsonElement Value,
        Dictionary<string, JsonElement>? NestedRecords,
        List<string>? VariantTypes = null);

    private sealed record VariantRecord(string Name, List<string> Types, int Position, JsonElement Value);

    private sealed class ComparisonCountsBuilder
    {
        public int Added { get; set; }
        public int Removed { get; set; }
        public int Modified { get; set; }
        public int Unchanged { get; set; }

        public ComparisonCounts Build() => new()
        {
            Added = Added,
            Removed = Removed,
            Modified = Modified,
            Unchanged = Unchanged
        };
    }
}
