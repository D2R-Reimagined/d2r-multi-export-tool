// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.Json.Nodes;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Builds deterministic field-level differences between JSON objects.</summary>
internal static class JsonStructuralDiff
{
    public static List<FieldChange> Compare(JsonElement oldValue, JsonElement newValue,
        IReadOnlySet<string> ignoredRootFields)
    {
        var changes = new List<FieldChange>();
        CompareAt(oldValue, newValue, "", changes, ignoredRootFields, atRoot: true);
        return changes;
    }

    private static void CompareAt(
        JsonElement oldValue,
        JsonElement newValue,
        string path,
        List<FieldChange> changes,
        IReadOnlySet<string> ignoredRootFields,
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

                if (!oldHasValue)
                    changes.Add(CreateChange(childPath, "Added", null, newProperty));
                else if (!newHasValue)
                    changes.Add(CreateChange(childPath, "Removed", oldProperty, null));
                else
                    CompareAt(oldProperty, newProperty, childPath, changes, ignoredRootFields);
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
                CompareAt(oldValue[index], newValue[index], $"{path}[{index}]", changes, ignoredRootFields);
            return;
        }

        var oldNode = JsonNode.Parse(oldValue.GetRawText());
        var newNode = JsonNode.Parse(newValue.GetRawText());
        if (!JsonNode.DeepEquals(oldNode, newNode))
            changes.Add(CreateChange(path, "Changed", oldValue, newValue));
    }

    private static FieldChange CreateChange(string path, string kind, JsonElement? oldValue, JsonElement? newValue) =>
        new()
        {
            Path = path,
            Kind = kind,
            OldValue = oldValue?.Clone(),
            NewValue = newValue?.Clone()
        };
}
