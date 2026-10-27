// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using static D2RMultiExport.Lib.Comparison.ComparisonMarkdown;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Renders the already-compared cube recipe records for human and AI review.</summary>
internal static class RecipeChangeMarkdown
{
    private const string CubeCategory = "cube-recipes";

    public static async Task<string> RenderAsync(ComparisonReport report)
    {
        var oldTranslations = await RecipeTranslations.LoadAsync(report.OldSnapshot);
        var newTranslations = await RecipeTranslations.LoadAsync(report.NewSnapshot);
        var markdown = new StringBuilder();
        var cube = report.Categories.FirstOrDefault(static category => category.Category == CubeCategory);

        markdown.AppendLine("# Cube recipe changes");
        markdown.AppendLine();
        AppendSummary(markdown, report, cube, oldTranslations, newTranslations);
        markdown.AppendLine("## Recipe identity limitation");
        markdown.AppendLine();
        markdown.AppendLine(
            "Recipe IDs are derived from recipe ingredients and conditions. Changing an ingredient or condition can therefore appear as one removed recipe and one added recipe, not as a confirmed modification. This report does not pair those records heuristically; review their original IDs, row indexes, and complete record JSON below and in `changes.json`.");
        markdown.AppendLine();

        if (cube?.Status != "Compared")
        {
            markdown.AppendLine("## Recipe changes unavailable");
            markdown.AppendLine();
            markdown.AppendLine(
                "**Cube recipes were not successfully compared. No recipe changes are reported here; this does not mean that there were no recipe changes.**");
            AppendIssues(markdown, cube?.Errors ?? [], "Comparison errors");
            AppendIssues(markdown, cube?.Warnings ?? [], "Comparison warnings");
            return markdown.ToString();
        }

        AppendRecordSection(markdown, "Added recipes", cube.AddedRecords, static record => record.Record,
            newTranslations, static record => record.RowIndex, static record => record.Key);
        AppendRecordSection(markdown, "Removed recipes", cube.RemovedRecords, static record => record.Record,
            oldTranslations, static record => record.RowIndex, static record => record.Key);

        markdown.AppendLine("## Modified recipes");
        markdown.AppendLine();
        if (cube.ModifiedRecords.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
        }
        else
        {
            foreach (var record in cube.ModifiedRecords)
            {
                markdown.Append("### Recipe ").Append(InlineCode(record.Key));
                markdown.Append(" (row ").Append(record.OldRowIndex?.ToString(CultureInfo.InvariantCulture) ?? "?");
                markdown.Append(" → ").Append(record.NewRowIndex?.ToString(CultureInfo.InvariantCulture) ?? "?")
                    .AppendLine(")");
                markdown.AppendLine();
                AppendFieldChanges(markdown, record.Changes);
                markdown.AppendLine("#### Before");
                markdown.AppendLine();
                AppendRecipe(markdown, record.Old, oldTranslations);
                markdown.AppendLine("#### After");
                markdown.AppendLine();
                AppendRecipe(markdown, record.New, newTranslations);
            }
        }

        return markdown.ToString();
    }

    private static void AppendSummary(
        StringBuilder markdown,
        ComparisonReport report,
        CategoryComparison? cube,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations)
    {
        markdown.AppendLine("## Summary");
        markdown.AppendLine();
        AppendSnapshotSummary(markdown, "Old", report.OldSnapshot);
        AppendSnapshotSummary(markdown, "New", report.NewSnapshot);
        markdown.Append("- Overall comparison: **").Append(report.Succeeded ? "succeeded" : "unsuccessful")
            .AppendLine("**.");
        markdown.Append("- Cube recipe comparison: **").Append(cube?.Status ?? "missing from report").AppendLine("**.");
        markdown.Append("- English translations (old snapshot): ").Append(EscapeMarkdown(oldTranslations.Status))
            .AppendLine(".");
        markdown.Append("- English translations (new snapshot): ").Append(EscapeMarkdown(newTranslations.Status))
            .AppendLine(".");
        if (cube?.Status == "Compared")
        {
            markdown.Append("- Cube file SHA-256: old ").Append(InlineCode(cube.OldFileSha256 ?? "unavailable"));
            markdown.Append(", new ").Append(InlineCode(cube.NewFileSha256 ?? "unavailable")).AppendLine(".");
            markdown.Append("- Recipe counts: ").Append(Invariant(cube.Counts.Added)).Append(" added, ")
                .Append(Invariant(cube.Counts.Removed)).Append(" removed, ").Append(Invariant(cube.Counts.Modified))
                .Append(" modified, ").Append(Invariant(cube.Counts.Unchanged)).Append(" unchanged, ")
                .Append(Invariant(cube.Counts.Reindexed))
                .AppendLine(" reindexed.");
        }
        else
            markdown.AppendLine(
                "- Recipe counts: unavailable because the cube category was not successfully compared.");

        markdown.AppendLine();
        var errors = CollectIssues(report, warnings: false);
        var warnings = CollectIssues(report, warnings: true);
        AppendIssues(markdown, errors, "Validation and comparison errors");
        AppendIssues(markdown, warnings, "Warnings");
    }

    private static List<ComparisonIssue> CollectIssues(ComparisonReport report, bool warnings)
    {
        var issues = new List<ComparisonIssue>();
        var seen = new HashSet<(string? Snapshot, string Code, string Message)>();

        void Add(IEnumerable<ComparisonIssue> source)
        {
            foreach (var issue in source)
            {
                if (seen.Add((issue.Snapshot, issue.Code, issue.Message))) issues.Add(issue);
            }
        }

        Add(warnings ? report.OldSnapshot.Warnings : report.OldSnapshot.Errors);
        Add(warnings ? report.NewSnapshot.Warnings : report.NewSnapshot.Errors);
        foreach (var category in report.Categories)
            Add(warnings ? category.Warnings : category.Errors);
        return issues;
    }

    private static void AppendRecordSection(
        StringBuilder markdown,
        string heading,
        IReadOnlyList<RecordEntry> records,
        Func<RecordEntry, JsonElement> getRecord,
        RecipeTranslations translations,
        Func<RecordEntry, int?> getRowIndex,
        Func<RecordEntry, string> getKey)
    {
        markdown.Append("## ").AppendLine(heading);
        markdown.AppendLine();
        if (records.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        foreach (var record in records)
        {
            markdown.Append("### Recipe ").Append(InlineCode(getKey(record))).Append(" (row ")
                .Append(getRowIndex(record)?.ToString(CultureInfo.InvariantCulture) ?? "?").AppendLine(")");
            markdown.AppendLine();
            AppendRecipe(markdown, getRecord(record), translations);
        }
    }

    private static void AppendFieldChanges(StringBuilder markdown, IReadOnlyList<FieldChange> changes)
    {
        markdown.AppendLine("**Changed fields**");
        markdown.AppendLine();
        if (changes.Count == 0)
        {
            markdown.AppendLine("*The report contains no field-level entries for this modification.*");
            markdown.AppendLine();
            return;
        }

        markdown.AppendLine("| Field | Change | Old value (raw JSON) | New value (raw JSON) |");
        markdown.AppendLine("|---|---|---|---|");
        foreach (var change in changes)
        {
            markdown.Append("| ").Append(EscapeTable(change.Path)).Append(" | ").Append(EscapeTable(change.Kind))
                .Append(" | ")
                .Append(EscapeTable(change.OldValue is { } oldValue ? CompactJson(oldValue) : "(absent)"))
                .Append(" | ")
                .Append(EscapeTable(change.NewValue is { } newValue ? CompactJson(newValue) : "(absent)"))
                .AppendLine(" |");
        }

        markdown.AppendLine();
    }

    private static void AppendRecipe(StringBuilder markdown, JsonElement record, RecipeTranslations translations)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            markdown.Append("- Record shape: expected an object; found ")
                .Append(EscapeMarkdown(record.ValueKind.ToString())).AppendLine(".");
            AppendRawRecord(markdown, record);
            return;
        }

        if (TryGet(record, "Class", out var classLine))
            AppendValueBullet(markdown, "Class", classLine, translations);
        if (TryGet(record, "RequiredClass", out var requiredClass))
            AppendValueBullet(markdown, "Required class", requiredClass, translations);
        if (TryGet(record, "Description", out var description))
            AppendValueBullet(markdown, "Description", description, translations);

        AppendRawField(markdown, record, "Op", "Operation");
        AppendRawField(markdown, record, "Param", "Parameter");
        AppendRawField(markdown, record, "Value", "Value");
        AppendRawField(markdown, record, "Enabled", "Enabled");
        AppendRawField(markdown, record, "NumInputs", "Declared input count");
        AppendRawField(markdown, record, "ResolvedInputsCount", "Resolved input count");

        if (TryGet(record, "Inputs", out var inputs))
            AppendInputs(markdown, inputs, translations);
        else
            markdown.AppendLine("- Inputs: field missing.");

        if (TryGet(record, "Outputs", out var outputs))
            AppendOutputs(markdown, outputs, translations);
        else
            markdown.AppendLine("- Outputs: field missing.");

        if (TryGet(record, "Notes", out var notes))
            AppendKeyedLineList(markdown, "Notes", notes, translations);

        AppendRawRecord(markdown, record);
    }

    private static void AppendInputs(StringBuilder markdown, JsonElement inputs, RecipeTranslations translations)
    {
        markdown.AppendLine("- Inputs:");
        if (inputs.ValueKind != JsonValueKind.Array)
        {
            markdown.Append("  - Unusual input data: ").AppendLine(InlineCode(RawJson(inputs)));
            return;
        }

        if (!inputs.EnumerateArray().Any())
        {
            markdown.AppendLine("  - (none)");
            return;
        }

        foreach (var input in inputs.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object)
            {
                markdown.Append("  - Unusual input record: ").AppendLine(InlineCode(RawJson(input)));
                continue;
            }

            var quantity = TryGet(input, "Quantity", out var quantityValue) ? RawJson(quantityValue) : "?";
            var name = TryGet(input, "Name", out var nameValue)
                ? RenderValue(nameValue, translations)
                : "<missing Name>";
            markdown.Append("  - ").Append(EscapeMarkdown(quantity)).Append(" × ").AppendLine(name);
            if (TryGet(input, "Qualifiers", out var qualifiers))
                AppendNestedKeyedLines(markdown, qualifiers, translations, "qualifier");
            if (TryGet(input, "RawToken", out var rawToken))
                markdown.Append("    - Raw token: ").AppendLine(EscapeMarkdown(RenderValue(rawToken, translations)));
        }
    }

    private static void AppendOutputs(StringBuilder markdown, JsonElement outputs, RecipeTranslations translations)
    {
        markdown.AppendLine("- Outputs:");
        if (outputs.ValueKind != JsonValueKind.Object)
        {
            markdown.Append("  - Unusual output data: ").AppendLine(InlineCode(RawJson(outputs)));
            return;
        }

        var found = false;
        foreach (var slot in new[] { "A", "B", "C" })
        {
            if (!TryGet(outputs, slot, out var output)) continue;
            found = true;
            AppendOutput(markdown, slot, output, translations);
        }

        if (!found) markdown.AppendLine("  - No A/B/C output slots are present.");
        foreach (var property in outputs.EnumerateObject())
        {
            if (property.Name is "A" or "B" or "C") continue;
            markdown.Append("  - Additional output field ").Append(InlineCode(property.Name)).Append(": ")
                .AppendLine(InlineCode(RawJson(property.Value)));
        }
    }

    private static void AppendOutput(StringBuilder markdown, string slot, JsonElement output,
        RecipeTranslations translations)
    {
        if (output.ValueKind == JsonValueKind.Null)
        {
            markdown.Append("  - Output ").Append(slot).AppendLine(": null.");
            return;
        }

        if (output.ValueKind != JsonValueKind.Object)
        {
            markdown.Append("  - Unusual output ").Append(slot).Append(": ").AppendLine(InlineCode(RawJson(output)));
            return;
        }

        var quantity = TryGet(output, "Quantity", out var quantityValue) ? RawJson(quantityValue) : "?";
        var name = TryGet(output, "Name", out var nameValue) ? RenderValue(nameValue, translations) : "<missing Name>";
        markdown.Append("  - Output ").Append(slot).Append(": ").Append(EscapeMarkdown(quantity)).Append(" × ")
            .AppendLine(name);
        if (TryGet(output, "Qualifiers", out var qualifiers))
            AppendNestedKeyedLines(markdown, qualifiers, translations, "qualifier");
        if (TryGet(output, "OutputChance", out var chance))
            markdown.Append("    - Output chance: ").AppendLine(EscapeMarkdown(RawJson(chance)));
        if (TryGet(output, "Lines", out var lines))
            AppendNestedKeyedLines(markdown, lines, translations, "property");
    }

    private static void AppendValueBullet(StringBuilder markdown, string label, JsonElement value,
        RecipeTranslations translations)
    {
        var rendered = value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())
            ? "(empty string in source)"
            : RenderValue(value, translations);
        markdown.Append("- ").Append(label).Append(": ").AppendLine(rendered);
    }

    private static void AppendRawField(StringBuilder markdown, JsonElement record, string field, string label)
    {
        if (!TryGet(record, field, out var value)) return;
        markdown.Append("- ").Append(label).Append(": ").AppendLine(InlineCode(RawJson(value)));
    }

    private static void AppendKeyedLineList(StringBuilder markdown, string label, JsonElement lines,
        RecipeTranslations translations)
    {
        markdown.Append("- ").AppendLine(label + ":");
        AppendNestedKeyedLines(markdown, lines, translations, "line");
    }
}
