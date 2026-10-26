// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Matches same-name magic affixes conservatively across two keyed snapshots.</summary>
internal static class MagicAffixMatcher
{
    public static MatchResult Match(string category, JsonElement oldRecords, JsonElement newRecords)
    {
        var errors = new List<ComparisonIssue>();
        var warnings = new List<ComparisonIssue>();
        var oldAffixes = ReadAffixes(category, oldRecords, "old", errors);
        var newAffixes = ReadAffixes(category, newRecords, "new", errors);
        if (errors.Count > 0)
            return new MatchResult([], [], errors, warnings);

        var oldByKey = new Dictionary<string, MatchedRecord>(StringComparer.Ordinal);
        var newByKey = new Dictionary<string, MatchedRecord>(StringComparer.Ordinal);
        var names = oldAffixes.Select(static affix => affix.Name)
            .Concat(newAffixes.Select(static affix => affix.Name))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        foreach (var name in names)
        {
            var oldGroup = oldAffixes.Where(affix => string.Equals(affix.Name, name, StringComparison.Ordinal))
                .ToList();
            var newGroup = newAffixes.Where(affix => string.Equals(affix.Name, name, StringComparison.Ordinal))
                .ToList();
            if (oldGroup.Count <= 1 && newGroup.Count <= 1)
            {
                if (oldGroup.Count == 1 && newGroup.Count == 1)
                    AddPair(oldGroup[0], newGroup[0], "NameKey", oldGroup, newGroup, oldByKey, newByKey);
                else if (oldGroup.Count == 1)
                    AddUnmatched(oldGroup[0], "old", oldGroup, newGroup, oldByKey, newByKey);
                else if (newGroup.Count == 1)
                    AddUnmatched(newGroup[0], "new", oldGroup, newGroup, oldByKey, newByKey);
                continue;
            }

            var remainingOld = new List<AffixRecord>(oldGroup);
            var remainingNew = new List<AffixRecord>(newGroup);
            var pairs = new List<AffixPair>();
            PairUnique(remainingOld, remainingNew, static affix => CreateProjection(affix, true, true, true),
                "PropertySignature", pairs);
            PairUnique(remainingOld, remainingNew, static affix => CreateProjection(affix, true, true, false),
                "StructuralMetadata", pairs);
            PairUnique(remainingOld, remainingNew, static affix => CreateProjection(affix, true, false, true),
                "PropertiesWithoutLevel", pairs);
            PairUnique(remainingOld, remainingNew, static affix => CreateProjection(affix, false, true, true),
                "PropertiesWithoutTypes", pairs);

            foreach (var pair in pairs)
                AddPair(pair.Old, pair.New, pair.MatchedBy, oldGroup, newGroup, oldByKey, newByKey);
            foreach (var affix in remainingOld)
                AddUnmatched(affix, "old", oldGroup, newGroup, oldByKey, newByKey);
            foreach (var affix in remainingNew)
                AddUnmatched(affix, "new", oldGroup, newGroup, oldByKey, newByKey);

            if (remainingOld.Count > 0 && remainingNew.Count > 0)
            {
                warnings.Add(new ComparisonIssue
                {
                    Code = "ambiguous-affix-identity",
                    Message =
                        $"The {category} name '{name}' has duplicate variants that could not be paired confidently. "
                        + $"Old candidates: {string.Join(", ", CandidateKeys(remainingOld, oldByKey.Values))}. "
                        + $"New candidates: {string.Join(", ", CandidateKeys(remainingNew, newByKey.Values))}. "
                        + "These records are reported as removals and additions rather than guessed matches."
                });
            }
        }

        return new MatchResult(oldByKey, newByKey, errors, warnings);
    }

    private static List<AffixRecord> ReadAffixes(
        string category,
        JsonElement records,
        string snapshot,
        List<ComparisonIssue> errors)
    {
        var result = new List<AffixRecord>();
        var position = 0;
        foreach (var record in records.EnumerateArray())
        {
            var currentPosition = position++;
            if (record.ValueKind != JsonValueKind.Object)
            {
                errors.Add(Issue("invalid-record",
                    $"The {snapshot} {category} record at array position {currentPosition} is not a JSON object.",
                    snapshot));
                continue;
            }

            if (!record.TryGetProperty("NameKey", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameValue.GetString()))
            {
                errors.Add(Issue("missing-record-identity",
                    $"The {snapshot} {category} record at array position {currentPosition} has no non-empty string NameKey.",
                    snapshot));
                continue;
            }

            var name = nameValue.GetString()!;
            if (!TryReadInt32(record, "Group", out var group) || !TryReadInt32(record, "Level", out var level) ||
                !TryReadStringArray(record, "Types", out var types) ||
                !record.TryGetProperty("Lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            {
                errors.Add(Issue("invalid-affix-identity",
                    $"The {snapshot} {category} record '{name}' at array position {currentPosition} must have integer Group and Level, a string array Types, and an array Lines.",
                    snapshot));
                continue;
            }

            types.Sort(StringComparer.Ordinal);
            result.Add(new AffixRecord(name, group, level, types, CreatePropertySignature(lines), currentPosition,
                record.Clone()));
        }

        return result;
    }

    private static bool TryReadInt32(JsonElement record, string field, out int value)
    {
        value = 0;
        return record.TryGetProperty(field, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out value);
    }

    private static bool TryReadStringArray(JsonElement record, string field, out List<string> values)
    {
        values = [];
        if (!record.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            values.Add(item.GetString() ?? "");
        }

        return true;
    }

    private static void PairUnique(
        List<AffixRecord> oldRecords,
        List<AffixRecord> newRecords,
        Func<AffixRecord, string> projection,
        string matchedBy,
        List<AffixPair> pairs)
    {
        var oldBuckets = oldRecords.GroupBy(projection, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        var newBuckets = newRecords.GroupBy(projection, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        var matchedOld = new HashSet<int>();
        var matchedNew = new HashSet<int>();

        foreach (var (key, oldBucket) in oldBuckets)
        {
            if (oldBucket.Count != 1 || !newBuckets.TryGetValue(key, out var newBucket) || newBucket.Count != 1)
                continue;
            pairs.Add(new AffixPair(oldBucket[0], newBucket[0], matchedBy));
            matchedOld.Add(oldBucket[0].Position);
            matchedNew.Add(newBucket[0].Position);
        }

        oldRecords.RemoveAll(record => matchedOld.Contains(record.Position));
        newRecords.RemoveAll(record => matchedNew.Contains(record.Position));
    }

    private static string CreateProjection(AffixRecord record, bool includeLevel, bool includeTypes,
        bool includeProperties)
    {
        var parts = new List<string> { record.Group.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (includeLevel) parts.Add(record.Level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (includeTypes) parts.Add(JsonSerializer.Serialize(record.Types));
        if (includeProperties) parts.Add(record.PropertySignature);
        return JsonSerializer.Serialize(parts);
    }

    private static string CreatePropertySignature(JsonElement lines)
    {
        var builder = new StringBuilder();
        builder.Append('[');
        var first = true;
        foreach (var line in lines.EnumerateArray())
        {
            if (!first) builder.Append(',');
            first = false;
            AppendWithoutNumbers(line, builder);
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static void AppendWithoutNumbers(JsonElement value, StringBuilder builder)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject()
                             .Where(static property => property.Value.ValueKind != JsonValueKind.Number)
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) builder.Append(',');
                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    AppendWithoutNumbers(property.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number) continue;
                    if (!firstItem) builder.Append(',');
                    firstItem = false;
                    AppendWithoutNumbers(item, builder);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(value.GetString()));
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                builder.Append("null");
                break;
        }
    }

    private static void AddPair(
        AffixRecord oldAffix,
        AffixRecord newAffix,
        string matchedBy,
        List<AffixRecord> oldGroup,
        List<AffixRecord> newGroup,
        Dictionary<string, MatchedRecord> oldByKey,
        Dictionary<string, MatchedRecord> newByKey)
    {
        var key = CreateReportKey(oldAffix, oldGroup, newGroup);
        if (oldByKey.ContainsKey(key) || newByKey.ContainsKey(key)) key = $"{key}#old-{oldAffix.Position}";
        var variants = oldGroup.Count > 1 || newGroup.Count > 1 ? oldAffix.Types : null;
        oldByKey.Add(key, new MatchedRecord(key, oldAffix.Value, variants, matchedBy, oldAffix.Position));
        newByKey.Add(key, new MatchedRecord(key, newAffix.Value, variants, matchedBy, newAffix.Position));
    }

    private static void AddUnmatched(
        AffixRecord affix,
        string side,
        List<AffixRecord> oldGroup,
        List<AffixRecord> newGroup,
        Dictionary<string, MatchedRecord> oldByKey,
        Dictionary<string, MatchedRecord> newByKey)
    {
        var key = CreateReportKey(affix, oldGroup, newGroup);
        var own = side == "old" ? oldByKey : newByKey;
        var opposite = side == "old" ? newByKey : oldByKey;
        if (own.ContainsKey(key) || opposite.ContainsKey(key)) key = $"{key}#{side}-{affix.Position}";
        var suffix = 1;
        var baseKey = key;
        while (own.ContainsKey(key) || opposite.ContainsKey(key)) key = $"{baseKey}-{suffix++}";
        var variants = oldGroup.Count > 1 || newGroup.Count > 1 ? affix.Types : null;
        own.Add(key, new MatchedRecord(key, affix.Value, variants, null, affix.Position));
    }

    private static IEnumerable<string> CandidateKeys(
        IEnumerable<AffixRecord> candidates,
        IEnumerable<MatchedRecord> records)
    {
        var positions = candidates.Select(static affix => affix.Position).ToHashSet();
        return records.Where(record => positions.Contains(record.Position))
            .OrderBy(static record => record.Position)
            .Select(static record => record.Key);
    }

    private static string CreateReportKey(AffixRecord affix, List<AffixRecord> oldGroup, List<AffixRecord> newGroup)
    {
        var structuralKey = CreateStructuralKey(affix);
        var needsProperties = oldGroup.Count(record => CreateStructuralKey(record) == structuralKey) > 1
                              || newGroup.Count(record => CreateStructuralKey(record) == structuralKey) > 1;
        var key = structuralKey;
        if (needsProperties) key += $",Properties={JsonSerializer.Serialize(affix.PropertySignature)}";
        return $"{JsonSerializer.Serialize(affix.Name)}[{key}]";
    }

    private static string CreateStructuralKey(AffixRecord affix) =>
        $"Group={affix.Group},Level={affix.Level},Types={JsonSerializer.Serialize(affix.Types)}";


    private static ComparisonIssue Issue(string code, string message, string snapshot) => new()
    {
        Code = code,
        Message = message,
        Snapshot = snapshot
    };

    internal sealed record MatchResult(
        Dictionary<string, MatchedRecord> OldRecords,
        Dictionary<string, MatchedRecord> NewRecords,
        List<ComparisonIssue> Errors,
        List<ComparisonIssue> Warnings);

    internal sealed record MatchedRecord(
        string Key,
        JsonElement Value,
        List<string>? VariantTypes,
        string? MatchedBy,
        int Position);

    private sealed record AffixRecord(
        string Name,
        int Group,
        int Level,
        List<string> Types,
        string PropertySignature,
        int Position,
        JsonElement Value);

    private sealed record AffixPair(AffixRecord Old, AffixRecord New, string MatchedBy);
}
