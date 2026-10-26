// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Builds deterministic field-level differences between JSON objects.</summary>
internal static class JsonStructuralDiff
{
    public static List<FieldChange> Compare(
        JsonElement oldValue,
        JsonElement newValue,
        IReadOnlySet<string> ignoredRootFields,
        string? keyedArrayField = null,
        string? keyedArrayMatchKey = null,
        bool keyedArrayOptional = false,
        IReadOnlySet<string>? unorderedRootArrays = null)
    {
        var changes = new List<FieldChange>();
        CompareAt(oldValue, newValue, "", changes, ignoredRootFields, keyedArrayField, keyedArrayMatchKey,
            keyedArrayOptional, unorderedRootArrays, atRoot: true);
        return changes;
    }

    private static void CompareAt(
        JsonElement oldValue,
        JsonElement newValue,
        string path,
        List<FieldChange> changes,
        IReadOnlySet<string> ignoredRootFields,
        string? keyedArrayField,
        string? keyedArrayMatchKey,
        bool keyedArrayOptional,
        IReadOnlySet<string>? unorderedRootArrays,
        bool atRoot = false)
    {
        if (oldValue.ValueKind != newValue.ValueKind)
        {
            changes.Add(CreateChange(path, "Changed", oldValue, newValue));
            return;
        }

        if (oldValue.ValueKind == JsonValueKind.Object)
        {
            var names = oldValue.EnumerateObject().Select(static p => p.Name)
                .Concat(newValue.EnumerateObject().Select(static p => p.Name))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);

            foreach (var name in names)
            {
                if (atRoot && ignoredRootFields.Contains(name)) continue;
                var oldHasValue = oldValue.TryGetProperty(name, out var oldProperty);
                var newHasValue = newValue.TryGetProperty(name, out var newProperty);
                var childPath = string.IsNullOrEmpty(path) ? name : $"{path}.{name}";

                if (atRoot && string.Equals(name, keyedArrayField, StringComparison.Ordinal)
                           && keyedArrayOptional
                           && IsOptionalArrayValue(oldHasValue, oldProperty)
                           && IsOptionalArrayValue(newHasValue, newProperty))
                    CompareKeyedArray(
                        oldHasValue && oldProperty.ValueKind == JsonValueKind.Array ? oldProperty : null,
                        newHasValue && newProperty.ValueKind == JsonValueKind.Array ? newProperty : null,
                        childPath, changes, ignoredRootFields, keyedArrayMatchKey!);
                else if (!oldHasValue)
                    changes.Add(CreateChange(childPath, "Added", null, newProperty));
                else if (!newHasValue)
                    changes.Add(CreateChange(childPath, "Removed", oldProperty, null));
                else if (atRoot && string.Equals(name, keyedArrayField, StringComparison.Ordinal)
                                && oldProperty.ValueKind == JsonValueKind.Array &&
                                newProperty.ValueKind == JsonValueKind.Array)
                    CompareKeyedArray(oldProperty, newProperty, childPath, changes, ignoredRootFields,
                        keyedArrayMatchKey!);
                else if (atRoot && unorderedRootArrays?.Contains(name) == true
                                && oldHasValue && newHasValue
                                && oldProperty.ValueKind == JsonValueKind.Array
                                && newProperty.ValueKind == JsonValueKind.Array
                                && TryCompareUnorderedStringArrays(oldProperty, newProperty, childPath, changes))
                    continue;
                else
                    CompareAt(oldProperty, newProperty, childPath, changes, ignoredRootFields,
                        keyedArrayField, keyedArrayMatchKey, keyedArrayOptional, unorderedRootArrays);
            }

            return;
        }

        if (oldValue.ValueKind == JsonValueKind.Array)
        {
            if (oldValue.GetArrayLength() != newValue.GetArrayLength())
            {
                changes.Add(CreateChange(path, "Changed", oldValue, newValue));
                return;
            }

            for (var index = 0; index < oldValue.GetArrayLength(); index++)
                CompareAt(oldValue[index], newValue[index], $"{path}[{index}]", changes, ignoredRootFields,
                    keyedArrayField, keyedArrayMatchKey, keyedArrayOptional, unorderedRootArrays);
            return;
        }

        var oldNode = JsonNode.Parse(oldValue.GetRawText());
        var newNode = JsonNode.Parse(newValue.GetRawText());
        if (!JsonNode.DeepEquals(oldNode, newNode))
            changes.Add(CreateChange(path, "Changed", oldValue, newValue));
    }

    private static void CompareKeyedArray(
        JsonElement? oldArray,
        JsonElement? newArray,
        string path,
        List<FieldChange> changes,
        IReadOnlySet<string> ignoredRootFields,
        string matchKey)
    {
        var oldByKey = oldArray is { ValueKind: JsonValueKind.Array } oldItems
            ? IndexByKey(oldItems, matchKey)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var newByKey = newArray is { ValueKind: JsonValueKind.Array } newItems
            ? IndexByKey(newItems, matchKey)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var keys = oldByKey.Keys.Concat(newByKey.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var childPath = $"{path}[{matchKey}=\"{EscapePathValue(key)}\"]";
            var hasOld = oldByKey.TryGetValue(key, out var oldRecord);
            var hasNew = newByKey.TryGetValue(key, out var newRecord);
            if (!hasOld)
                changes.Add(CreateChange(childPath, "Added", null, newRecord));
            else if (!hasNew)
                changes.Add(CreateChange(childPath, "Removed", oldRecord, null));
            else
                CompareAt(oldRecord, newRecord, childPath, changes, ignoredRootFields, null, null,
                    keyedArrayOptional: false, unorderedRootArrays: null);
        }
    }

    private static bool IsOptionalArrayValue(bool hasValue, JsonElement value) =>
        !hasValue || value.ValueKind is JsonValueKind.Null or JsonValueKind.Array;

    private static bool TryCompareUnorderedStringArrays(
        JsonElement oldArray,
        JsonElement newArray,
        string path,
        List<FieldChange> changes)
    {
        if (oldArray.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String) ||
            newArray.EnumerateArray().Any(static value => value.ValueKind != JsonValueKind.String))
            return false;

        var oldCounts = CountStrings(oldArray);
        var newCounts = CountStrings(newArray);
        foreach (var value in oldCounts.Keys.Concat(newCounts.Keys).Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var difference = newCounts.GetValueOrDefault(value) - oldCounts.GetValueOrDefault(value);
            var kind = difference > 0 ? "Added" : "Removed";
            for (var occurrence = 0; occurrence < Math.Abs(difference); occurrence++)
            {
                var oldValue = difference < 0 ? CreateStringValue(value) : (JsonElement?)null;
                var newValue = difference > 0 ? CreateStringValue(value) : (JsonElement?)null;
                changes.Add(CreateChange($"{path}[Value=\"{EscapePathValue(value)}\"]", kind,
                    oldValue, newValue));
            }
        }

        return true;
    }

    private static Dictionary<string, int> CountStrings(JsonElement array)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in array.EnumerateArray())
        {
            var text = value.GetString() ?? "";
            counts[text] = counts.GetValueOrDefault(text) + 1;
        }

        return counts;
    }

    private static JsonElement CreateStringValue(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> IndexByKey(JsonElement array, string matchKey)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
            result.Add(item.GetProperty(matchKey).GetString()!, item);
        return result;
    }

    private static string EscapePathValue(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static FieldChange CreateChange(string path, string kind, JsonElement? oldValue, JsonElement? newValue) =>
        new()
        {
            Path = path,
            Kind = kind,
            OldValue = oldValue?.Clone(),
            NewValue = newValue?.Clone()
        };
}
