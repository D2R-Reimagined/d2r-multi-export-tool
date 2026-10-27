// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using static D2RMultiExport.Lib.Comparison.ComparisonMarkdown;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Renders item category changes from the authoritative schema-2 comparison report.</summary>
internal static class ItemChangeMarkdown
{
    /// <summary>The item categories and display names represented by this report.</summary>
    private static readonly CategoryDefinition[] CategoryDefinitions =
    [
        new("uniques", "Unique items"),
        new("sets", "Sets and set pieces"),
        new("runewords", "Runewords"),
        new("armors", "Base armor"),
        new("weapons", "Base weapons"),
        new("magicprefix", "Magic prefixes"),
        new("magicsuffix", "Magic suffixes")
    ];

    public static async Task<string> RenderAsync(ComparisonReport report)
    {
        var oldTranslations = await RecipeTranslations.LoadAsync(report.OldSnapshot);
        var newTranslations = await RecipeTranslations.LoadAsync(report.NewSnapshot);
        var markdown = new StringBuilder();
        markdown.AppendLine("# Item changes");
        markdown.AppendLine();
        AppendSummary(markdown, report, oldTranslations, newTranslations);

        foreach (var definition in CategoryDefinitions)
        {
            var category = report.Categories.FirstOrDefault(item => item.Category == definition.Id);
            AppendCategory(markdown, category, definition, oldTranslations, newTranslations);
        }

        return markdown.ToString();
    }

    private static void AppendSummary(
        StringBuilder markdown,
        ComparisonReport report,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations)
    {
        markdown.AppendLine("## Summary");
        markdown.AppendLine();
        AppendSnapshotSummary(markdown, "Old", report.OldSnapshot);
        AppendSnapshotSummary(markdown, "New", report.NewSnapshot);
        markdown.Append("- Overall comparison: **").Append(report.Succeeded ? "succeeded" : "unsuccessful")
            .AppendLine("**.");
        markdown.Append("- English translations (old snapshot): ").Append(EscapeMarkdown(oldTranslations.Status))
            .AppendLine(".");
        markdown.Append("- English translations (new snapshot): ").Append(EscapeMarkdown(newTranslations.Status))
            .AppendLine(".");
        markdown.AppendLine();
        markdown.AppendLine("### Item counts by category");
        markdown.AppendLine();
        markdown.AppendLine("| Category | Status | Added | Removed | Modified | Unchanged | Nested changes |");
        markdown.AppendLine("|---|---|---:|---:|---:|---:|---|");
        foreach (var definition in CategoryDefinitions)
        {
            var category = report.Categories.FirstOrDefault(item => item.Category == definition.Id);
            markdown.Append("| ").Append(EscapeTable(definition.Title)).Append(" (`")
                .Append(EscapeTable(definition.Id)).Append("`) | ")
                .Append(EscapeTable(category?.Status ?? "NotCompared")).Append(" | ");
            if (category?.Status != "Compared")
                markdown.Append("unavailable | unavailable | unavailable | unavailable | unavailable |").AppendLine();
            else
            {
                var counts = category.Counts;
                markdown.Append(Invariant(counts.Added)).Append(" | ").Append(Invariant(counts.Removed)).Append(" | ")
                    .Append(Invariant(counts.Modified)).Append(" | ").Append(Invariant(counts.Unchanged)).Append(" | ");
                if (category.NestedCounts is { } nested)
                    markdown.Append(Invariant(nested.Added)).Append(" added, ").Append(Invariant(nested.Removed))
                        .Append(" removed, ").Append(Invariant(nested.Modified)).Append(" modified");
                else
                    markdown.Append("not applicable");
                markdown.AppendLine(" |");
            }
        }

        markdown.AppendLine();
        markdown.AppendLine(
            "A modification means the comparator paired the same stable identity. An identity change reported as a removal and addition remains separate; this Markdown does not infer replacement pairs.");
        markdown.AppendLine();

        var errors = CollectIssues(report, warnings: false);
        var warnings = CollectIssues(report, warnings: true);
        AppendIssues(markdown, errors, "Validation and comparison errors");
        AppendIssues(markdown, warnings, "Warnings and ambiguous identity matches");
    }

    private static List<ComparisonIssue> CollectIssues(ComparisonReport report, bool warnings)
    {
        var issues = new List<ComparisonIssue>();
        var seen = new HashSet<(string Category, string? Snapshot, string Code, string Message)>();

        void Add(IEnumerable<ComparisonIssue> source, string category)
        {
            foreach (var issue in source)
                if (seen.Add((category, issue.Snapshot, issue.Code, issue.Message)))
                    issues.Add(new ComparisonIssue
                    {
                        Snapshot = issue.Snapshot,
                        Code = issue.Code,
                        Message = category.Length == 0 ? issue.Message : $"[{category}] {issue.Message}"
                    });
        }

        Add(warnings ? report.OldSnapshot.Warnings : report.OldSnapshot.Errors, "");
        Add(warnings ? report.NewSnapshot.Warnings : report.NewSnapshot.Errors, "");
        foreach (var definition in CategoryDefinitions)
        {
            var category = report.Categories.FirstOrDefault(item => item.Category == definition.Id);
            if (category is not null) Add(warnings ? category.Warnings : category.Errors, definition.Id);
        }

        return issues;
    }

    private static void AppendCategory(
        StringBuilder markdown,
        CategoryComparison? category,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations)
    {
        markdown.Append("## ").Append(definition.Title).Append(" (").Append(InlineCode(definition.Id)).AppendLine(")");
        markdown.AppendLine();
        if (category?.Status != "Compared")
        {
            markdown.Append("**Comparison unavailable: ")
                .Append(EscapeMarkdown(category?.Status ?? "missing from report"))
                .AppendLine(".**");
            markdown.AppendLine();
            markdown.AppendLine(
                "This category was not successfully compared. No item changes are listed; this does not mean that there were no changes.");
            markdown.AppendLine();
            AppendCategoryIssues(markdown, category);
            return;
        }

        AppendRecordSection(markdown, "Added", category.AddedRecords, definition, newTranslations, category);
        AppendRecordSection(markdown, "Removed", category.RemovedRecords, definition, oldTranslations, category);
        markdown.AppendLine("### Modified");
        markdown.AppendLine();
        if (category.ModifiedRecords.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        foreach (var record in category.ModifiedRecords)
            AppendModifiedRecord(markdown, record, definition, oldTranslations, newTranslations);
    }

    private static void AppendCategoryIssues(StringBuilder markdown, CategoryComparison? category)
    {
        if (category is null) return;
        if (category.Errors.Count > 0) AppendIssues(markdown, category.Errors, "Category comparison errors");
        if (category.Warnings.Count > 0) AppendIssues(markdown, category.Warnings, "Category comparison warnings");
    }

    private static void AppendRecordSection(
        StringBuilder markdown,
        string heading,
        IReadOnlyList<RecordEntry> records,
        CategoryDefinition definition,
        RecipeTranslations translations,
        CategoryComparison category)
    {
        markdown.Append("### ").AppendLine(heading);
        markdown.AppendLine();
        if (records.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        foreach (var record in records)
        {
            markdown.Append("#### ").AppendLine(GetRecordName(record.Record, translations, record.Key));
            markdown.AppendLine();
            AppendIdentity(markdown, record.Key, record.VariantTypes, null);
            markdown.AppendLine();
            if (IsAmbiguousCandidate(record.Key, definition.Id, category))
                markdown.AppendLine(
                    "- Identity status: unresolved ambiguous candidate; listed as a removal/addition by the comparator, not a confirmed item removal/addition.");
            AppendRecordFields(markdown, record.Record, translations, definition, metadataOnly: false,
                skipNested: false);
            markdown.AppendLine();
            AppendRawRecord(markdown, record.Record);
        }
    }

    private static void AppendModifiedRecord(
        StringBuilder markdown,
        ModifiedRecord record,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations)
    {
        markdown.Append("#### ").AppendLine(GetRecordName(record.New, newTranslations, record.Key));
        markdown.AppendLine();
        AppendIdentity(markdown, record.Key, record.VariantTypes, record.MatchedBy);
        markdown.AppendLine();
        if (definition.Id is "magicprefix" or "magicsuffix" && record.MatchedBy is not null &&
            record.MatchedBy != "NameKey")
            markdown.Append("- Affix paired by comparator identity pass: ").AppendLine(InlineCode(record.MatchedBy));
        markdown.AppendLine();

        markdown.AppendLine("**Identifying and non-property metadata — before**");
        markdown.AppendLine();
        AppendRecordFields(markdown, record.Old, oldTranslations, definition, metadataOnly: true,
            skipNested: definition.Id is "sets" or "armors" or "weapons");
        markdown.AppendLine();
        markdown.AppendLine("**Identifying and non-property metadata — after**");
        markdown.AppendLine();
        AppendRecordFields(markdown, record.New, newTranslations, definition, metadataOnly: true,
            skipNested: definition.Id is "sets" or "armors" or "weapons");
        markdown.AppendLine();

        var nestedField = record.NestedChanges?.Field;
        var visibleChanges = record.Changes
            .Where(change => nestedField is null || !IsNestedPath(change.Path, nestedField))
            .ToList();
        AppendFieldChanges(markdown, visibleChanges, record.Old, record.New, oldTranslations, newTranslations);
        if (record.NestedChanges is { } nestedChanges)
            AppendNestedChanges(markdown, nestedChanges, oldTranslations, newTranslations,
                definition.Id is "armors" or "weapons");

        AppendRawRecord(markdown, record.Old, "Complete original record JSON (before)");
        AppendRawRecord(markdown, record.New, "Complete original record JSON (after)");
    }

    private static void AppendIdentity(StringBuilder markdown, string key, IReadOnlyList<string>? variants,
        string? matchedBy)
    {
        markdown.Append("- Stable comparison identity: ").AppendLine(InlineCode(key));
        if (variants is { Count: > 0 })
            markdown.Append("- VariantTypes (preserved by the comparator): ")
                .AppendLine(string.Join(", ", variants.Select(InlineCode)));
        if (matchedBy is not null)
            markdown.Append("- MatchedBy: ").AppendLine(InlineCode(matchedBy));
    }

    private static void AppendRecordFields(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        CategoryDefinition definition,
        bool metadataOnly,
        bool skipNested)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            markdown.Append("- Record shape: ").AppendLine(InlineCode(RawJson(record)));
            return;
        }

        foreach (var property in record.EnumerateObject())
        {
            if (metadataOnly && IsPropertyField(property.Name)) continue;
            if (skipNested && string.Equals(property.Name, definition.Id == "sets" ? "SetItems" : "AutoMagicGroups",
                    StringComparison.Ordinal))
                continue;
            AppendValue(markdown, property.Name, property.Value, translations, depth: 1, metadataOnly: metadataOnly);
        }
    }

    private static void AppendValue(StringBuilder markdown, string label, JsonElement value,
        RecipeTranslations translations, int depth, bool metadataOnly = false)
    {
        var indent = new string(' ', depth * 2);
        if (IsKeyedLine(value))
        {
            markdown.Append(indent).Append("- ").Append(InlineCode(label)).Append(": ")
                .AppendLine(RenderKeyedLine(value, translations));
            return;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            markdown.Append(indent).Append("- ").Append(InlineCode(label)).AppendLine(":");
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var itemLabel = string.Equals(label, "Runes", StringComparison.Ordinal)
                    ? $"Rune {index + 1} (sequence order)"
                    : $"[{index}]";
                if (item.ValueKind == JsonValueKind.Object && !IsKeyedLine(item))
                {
                    markdown.Append(indent).Append("  - ").Append(InlineCode(itemLabel)).AppendLine(":");
                    foreach (var child in item.EnumerateObject())
                    {
                        if (metadataOnly && IsPropertyField(child.Name)) continue;
                        AppendValue(markdown, child.Name, child.Value, translations, depth + 2, metadataOnly);
                    }
                }
                else
                    AppendValue(markdown, itemLabel, item, translations, depth + 1, metadataOnly);

                index++;
            }

            if (index == 0) markdown.Append(indent).AppendLine("  - (none)");
            return;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            markdown.Append(indent).Append("- ").Append(InlineCode(label)).AppendLine(":");
            foreach (var property in value.EnumerateObject())
            {
                if (metadataOnly && IsPropertyField(property.Name)) continue;
                AppendValue(markdown, property.Name, property.Value, translations, depth + 1, metadataOnly);
            }

            return;
        }

        var rendered = string.Equals(label, "NameKey", StringComparison.Ordinal)
                       && value.ValueKind == JsonValueKind.String
            ? RenderNameKey(value.GetString() ?? "", translations)
            : value.ValueKind == JsonValueKind.String
                ? EscapeMarkdown(value.GetString() ?? "")
                : EscapeMarkdown(RawJson(value));
        markdown.Append(indent).Append("- ").Append(InlineCode(label)).Append(": ").AppendLine(rendered);
    }

    private static void AppendFieldChanges(
        StringBuilder markdown,
        IReadOnlyList<FieldChange> changes,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations)
    {
        markdown.AppendLine("**Readable before/after field changes**");
        markdown.AppendLine();
        if (changes.Count == 0)
        {
            markdown.AppendLine("*No direct field differences; nested changes are shown separately.*");
            markdown.AppendLine();
            return;
        }

        var renderedLinePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var hasOldLine = TryFindKeyedLine(oldRecord, change.Path, out var oldLine, out var oldLinePath);
            var hasNewLine = TryFindKeyedLine(newRecord, change.Path, out var newLine, out var newLinePath);
            if (hasOldLine || hasNewLine)
            {
                var linePath = hasNewLine ? newLinePath : oldLinePath;
                if (!renderedLinePaths.Add(linePath)) continue;
                markdown.Append("#### Property line ").AppendLine(InlineCode(linePath));
                markdown.AppendLine();
                AppendBeforeAfter(markdown,
                    hasOldLine ? RenderKeyedLine(oldLine, oldTranslations) : "(absent)",
                    hasNewLine ? RenderKeyedLine(newLine, newTranslations) : "(absent)");
                continue;
            }

            markdown.Append("- Field ").AppendLine(InlineCode(change.Path));
            var oldValue = change.OldValue is { } oldRaw ? RenderChangedValue(oldRaw, oldTranslations) : "(absent)";
            var newValue = change.NewValue is { } newRaw ? RenderChangedValue(newRaw, newTranslations) : "(absent)";
            AppendBeforeAfter(markdown, oldValue, newValue);
        }

        markdown.AppendLine();
    }

    private static string RenderChangedValue(JsonElement value, RecipeTranslations translations)
    {
        if (IsKeyedLine(value)) return RenderKeyedLine(value, translations);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = value.EnumerateObject()
                .Select(property => $"{InlineCode(property.Name)}: {RenderChangedValue(property.Value, translations)}");
            return "{" + string.Join("; ", properties) + "}";
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var values = value.EnumerateArray().Select(item => RenderChangedValue(item, translations));
            return "[" + string.Join("; ", values) + "]";
        }

        if (value.ValueKind == JsonValueKind.String) return InlineCode(value.GetString() ?? "");
        return EscapeMarkdown(RawJson(value));
    }

    private static void AppendBeforeAfter(StringBuilder markdown, string before, string after)
    {
        markdown.Append("  - Before: ").AppendLine(before);
        markdown.Append("  - After: ").AppendLine(after);
        markdown.AppendLine();
    }

    private static void AppendNestedChanges(StringBuilder markdown, NestedRecordChanges changes,
        RecipeTranslations oldTranslations, RecipeTranslations newTranslations, bool automagic)
    {
        markdown.Append("**Nested ").Append(EscapeMarkdown(changes.Field)).AppendLine(" changes**");
        markdown.AppendLine();
        if (automagic)
            markdown.AppendLine(
                "These are possible automagic groups associated with the base item; they do not guarantee that every group or affix will roll.");
        markdown.AppendLine();
        AppendNestedEntries(markdown, "Added", changes.Added, oldTranslations, newTranslations);
        AppendNestedEntries(markdown, "Removed", changes.Removed, oldTranslations, newTranslations);
        AppendNestedModified(markdown, changes.Modified, oldTranslations, newTranslations);
    }

    private static void AppendNestedEntries(StringBuilder markdown, string heading, IReadOnlyList<RecordEntry> records,
        RecipeTranslations oldTranslations, RecipeTranslations newTranslations)
    {
        markdown.Append("### Nested ").AppendLine(heading);
        markdown.AppendLine();
        if (records.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        var translations = heading == "Removed" ? oldTranslations : newTranslations;
        foreach (var record in records)
        {
            markdown.Append("#### ").AppendLine(GetRecordName(record.Record, translations, record.Key));
            markdown.AppendLine();
            AppendIdentity(markdown, record.Key, record.VariantTypes, null);
            markdown.AppendLine();
            AppendRecordFields(markdown, record.Record, translations, new CategoryDefinition("", ""),
                metadataOnly: false, skipNested: false);
            markdown.AppendLine();
            AppendRawRecord(markdown, record.Record);
        }
    }

    private static void AppendNestedModified(StringBuilder markdown, IReadOnlyList<ModifiedRecord> records,
        RecipeTranslations oldTranslations, RecipeTranslations newTranslations)
    {
        markdown.AppendLine("### Nested Modified");
        markdown.AppendLine();
        if (records.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        foreach (var record in records)
        {
            markdown.Append("#### ").AppendLine(GetRecordName(record.New, newTranslations, record.Key));
            markdown.AppendLine();
            AppendIdentity(markdown, record.Key, record.VariantTypes, record.MatchedBy);
            markdown.AppendLine();
            AppendFieldChanges(markdown, record.Changes, record.Old, record.New, oldTranslations, newTranslations);
            AppendRawRecord(markdown, record.Old, "Complete original nested record JSON (before)");
            AppendRawRecord(markdown, record.New, "Complete original nested record JSON (after)");
        }
    }

    private static string GetRecordName(JsonElement record, RecipeTranslations translations, string fallback)
    {
        if (TryGet(record, "NameKey", out var nameKey) && nameKey.ValueKind == JsonValueKind.String)
            return RenderNameKey(nameKey.GetString() ?? "", translations);
        if (TryGet(record, "Name", out var name))
            return IsKeyedLine(name) ? RenderKeyedLine(name, translations) : RenderValue(name, translations);
        foreach (var field in new[] { "Index", "Code" })
            if (TryGet(record, field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var candidate = value.GetString() ?? "";
                return translations.TryGet(candidate, out var localized)
                    ? EscapeMarkdown(localized)
                    : EscapeMarkdown(candidate);
            }

        return EscapeMarkdown(fallback);
    }

    private static string RenderNameKey(string key, RecipeTranslations translations) =>
        EscapeMarkdown(translations.TryGet(key, out var localized) ? localized : key);

    private static bool IsAmbiguousCandidate(string key, string category, CategoryComparison comparison) =>
        category is "magicprefix" or "magicsuffix"
        && key.Contains('#')
        && comparison.Warnings.Any(static issue => issue.Code == "ambiguous-affix-identity");

    private static bool IsNestedPath(string path, string field) =>
        path.StartsWith(field + "[", StringComparison.Ordinal)
        || string.Equals(path, field, StringComparison.Ordinal);

    private static bool IsPropertyField(string field) =>
        field is "Lines" or "PartialBonuses" or "FullBonuses" or "SetBonuses";

    private static bool IsKeyedLine(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && (TryGet(value, "key", out var key) && key.ValueKind == JsonValueKind.String
            || TryGet(value, "nameKey", out var nameKey) && nameKey.ValueKind == JsonValueKind.String)
        && TryGet(value, "args", out var args) && args.ValueKind == JsonValueKind.Array;

    private static bool TryFindKeyedLine(JsonElement record, string path, out JsonElement line, out string linePath)
    {
        line = default;
        linePath = "";
        if (!TryParsePath(path, out var steps)) return false;
        var current = record;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step.Property is { } property)
            {
                if (!TryGet(current, property, out current)) return false;
            }
            else if (step.Index is { } arrayIndex)
            {
                if (current.ValueKind != JsonValueKind.Array || arrayIndex >= current.GetArrayLength()) return false;
                current = current[arrayIndex];
            }
            else
                return false;

            if (IsKeyedLine(current))
            {
                line = current;
                linePath = FormatPath(steps.Take(index + 1));
            }
        }

        return line.ValueKind != JsonValueKind.Undefined;
    }

    private static string FormatPath(IEnumerable<PathStep> steps)
    {
        var path = new StringBuilder();
        foreach (var step in steps)
        {
            if (step.Property is { } property)
            {
                if (path.Length > 0) path.Append('.');
                path.Append(property);
            }
            else
                path.Append('[').Append(step.Index).Append(']');
        }

        return path.ToString();
    }

    private static bool TryParsePath(string path, out List<PathStep> steps)
    {
        steps = [];
        var position = 0;
        while (position < path.Length)
        {
            if (path[position] == '.')
            {
                position++;
                continue;
            }

            if (path[position] == '[')
            {
                var close = path.IndexOf(']', position + 1);
                if (close < 0 || !int.TryParse(path.AsSpan(position + 1, close - position - 1),
                        NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    return false;
                steps.Add(new PathStep(null, index));
                position = close + 1;
                continue;
            }

            var end = position;
            while (end < path.Length && path[end] is not '.' and not '[') end++;
            if (end == position) return false;
            steps.Add(new PathStep(path[position..end], null));
            position = end;
        }

        return steps.Count > 0;
    }

    private sealed record CategoryDefinition(string Id, string Title);

    private sealed record PathStep(string? Property, int? Index)
    {
        public override string ToString() => Property is not null ? Property : $"[{Index}]";
    }
}
