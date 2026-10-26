// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Renders the already-compared cube recipe records for human and AI review.</summary>
internal static class RecipeChangeMarkdown
{
    private const string CubeCategory = "cube-recipes";
    private const string PerLevelSuffixKey = "strPerCharacterLevelSuffix";

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

    private static void AppendSnapshotSummary(StringBuilder markdown, string label, SnapshotInfo snapshot)
    {
        markdown.Append("- ").Append(label).Append(" snapshot: ").Append(InlineCode(snapshot.Path))
            .Append("; manifest schema ")
            .Append(snapshot.ManifestSchemaVersion?.ToString() ?? "unavailable").Append("; Succeeded=")
            .Append(snapshot.Succeeded?.ToString() ?? "unavailable").Append("; Complete=")
            .Append(snapshot.Complete?.ToString() ?? "unavailable").Append("; manifest SHA-256 ")
            .Append(InlineCode(snapshot.ManifestSha256 ?? "unavailable")).AppendLine(".");
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

    private static void AppendIssues(StringBuilder markdown, IReadOnlyCollection<ComparisonIssue> issues,
        string heading)
    {
        markdown.Append("### ").AppendLine(heading);
        markdown.AppendLine();
        if (issues.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        foreach (var issue in issues)
        {
            markdown.Append("- ");
            if (!string.IsNullOrWhiteSpace(issue.Snapshot))
                markdown.Append('[').Append(EscapeMarkdown(issue.Snapshot)).Append("] ");
            markdown.Append(InlineCode(issue.Code)).Append(": ").AppendLine(EscapeMarkdown(issue.Message));
        }

        markdown.AppendLine();
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

    private static void AppendNestedKeyedLines(
        StringBuilder markdown,
        JsonElement lines,
        RecipeTranslations translations,
        string label,
        int depth = 2)
    {
        var indent = new string(' ', depth * 2);
        if (lines.ValueKind != JsonValueKind.Array)
        {
            markdown.Append(indent).Append("- Unusual ").Append(label).Append(" data: ")
                .AppendLine(InlineCode(RawJson(lines)));
            return;
        }

        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object)
            {
                markdown.Append(indent).Append("- ").AppendLine(InlineCode(RawJson(line)));
                continue;
            }

            markdown.Append(indent).Append("- ").AppendLine(RenderKeyedLine(line, translations));
            if (TryGet(line, "children", out var children))
            {
                if (TryGet(line, "pickMode", out var pickMode))
                    markdown.Append(indent).Append("  - Pick mode: ").AppendLine(EscapeMarkdown(RawJson(pickMode)));
                AppendNestedKeyedLines(markdown, children, translations, "child property", depth + 1);
            }
        }
    }

    private static string RenderValue(JsonElement value, RecipeTranslations translations) =>
        value.ValueKind == JsonValueKind.Object
            ? RenderKeyedLine(value, translations)
            : EscapeMarkdown(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : RawJson(value));

    private static string RenderKeyedLine(JsonElement line, RecipeTranslations translations)
    {
        var key = TryGet(line, "key", out var keyValue) && keyValue.ValueKind == JsonValueKind.String
            ? keyValue.GetString() ?? ""
            : "";
        if (key.Length == 0 && TryGet(line, "nameKey", out var nameKeyValue) &&
            nameKeyValue.ValueKind == JsonValueKind.String)
            key = nameKeyValue.GetString() ?? "";

        var args = TryGet(line, "args", out var argsValue) && argsValue.ValueKind == JsonValueKind.Array
            ? argsValue.EnumerateArray().ToArray()
            : [];
        var rendered = translations.TryGet(key, out var template) &&
                       TryFormatTemplate(template, args, translations, out var formatted)
            ? formatted
            : $"{(key.Length == 0 ? "<empty key>" : key)} [{string.Join(", ", args.Select(RawJson))}]";

        var annotations = new List<string>();
        if (TryGet(line, "qualifier", out var qualifier)) annotations.Add($"qualifier={RawJson(qualifier)}");
        if (TryGet(line, "selection", out var selection)) annotations.Add($"selection={RawJson(selection)}");
        if (TryGet(line, "itemsRequired", out var itemsRequired))
            annotations.Add($"itemsRequired={RawJson(itemsRequired)}");
        if (TryGet(line, "fullSet", out var fullSet)) annotations.Add($"fullSet={RawJson(fullSet)}");
        if (TryGet(line, "code", out var code)) annotations.Add($"code={RawJson(code)}");
        if (TryGet(line, "chance", out var chance)) annotations.Add($"chance={RawJson(chance)}");
        if (TryGet(line, "pickMode", out var pickMode)) annotations.Add($"pickMode={RawJson(pickMode)}");
        if (TryGet(line, "classOnly", out var classOnly) && classOnly.ValueKind == JsonValueKind.String)
        {
            var classKey = classOnly.GetString() ?? "";
            annotations.Add(translations.TryGet(classKey, out var classText) ? classText : $"classOnly={classKey}");
        }

        if (TryGet(line, "perLevel", out var perLevel) && perLevel.ValueKind == JsonValueKind.True)
            annotations.Add(translations.TryGet(PerLevelSuffixKey, out var suffix) ? suffix : "perLevel=true");

        if (annotations.Count > 0) rendered += " (" + string.Join("; ", annotations) + ")";
        return EscapeMarkdown(rendered);
    }

    private static bool TryFormatTemplate(
        string template,
        JsonElement[] args,
        RecipeTranslations translations,
        out string formatted)
    {
        var tokens = new List<string>();
        var literals = new StringBuilder();
        var segments = new List<(string Literal, string? Token)>();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '%')
            {
                literals.Append(template[index]);
                continue;
            }

            if (index + 1 >= template.Length)
            {
                formatted = "";
                return false;
            }

            if (template[index + 1] == '%')
            {
                literals.Append('%');
                index++;
                continue;
            }

            string token;
            if (template[index + 1] == '+' && index + 2 < template.Length && template[index + 2] == 'd')
            {
                token = "+d";
                index += 2;
            }
            else if (template[index + 1] is 'd' or 'i' or 's')
            {
                token = template[++index].ToString();
            }
            else
            {
                formatted = "";
                return false;
            }

            segments.Add((literals.ToString(), token));
            literals.Clear();
            tokens.Add(token);
        }

        segments.Add((literals.ToString(), null));
        if (tokens.Count == 1 && (tokens[0] is "d" or "+d" or "i") && args.Length == 2
            && IsNumber(args[0]) && IsNumber(args[1]))
        {
            var range = (tokens[0] == "+d" ? "+(" : "(") + RawJson(args[0]) + "-" + RawJson(args[1]) + ")";
            formatted = segments[0].Literal + range + segments[1].Literal;
            return true;
        }

        if (tokens.Count != args.Length)
        {
            formatted = "";
            return false;
        }

        var result = new StringBuilder();
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var argument = args[index];
            if (token == "s")
            {
                if (argument.ValueKind != JsonValueKind.String ||
                    !translations.TryGet(argument.GetString() ?? "", out var text))
                {
                    formatted = "";
                    return false;
                }

                result.Append(segments[index].Literal).Append(text);
            }
            else
            {
                if (!IsNumber(argument))
                {
                    formatted = "";
                    return false;
                }

                var number = RawJson(argument);
                result.Append(segments[index].Literal);
                if (token == "+d" && !number.StartsWith("-", StringComparison.Ordinal)) result.Append('+');
                result.Append(number);
            }
        }

        formatted = result.Append(segments[^1].Literal).ToString();
        return true;
    }

    private static bool IsNumber(JsonElement value) => value.ValueKind == JsonValueKind.Number;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void AppendRawRecord(StringBuilder markdown, JsonElement record)
    {
        var json = RawJson(record);
        var fenceLength = Math.Max(3, LongestBacktickRun(json) + 1);
        var fence = new string('`', fenceLength);
        markdown.AppendLine("<details>");
        markdown.AppendLine("<summary>Complete original record JSON</summary>");
        markdown.AppendLine();
        markdown.Append(fence).AppendLine("json");
        markdown.AppendLine(json);
        markdown.AppendLine(fence);
        markdown.AppendLine();
        markdown.AppendLine("</details>");
        markdown.AppendLine();
    }

    private static int LongestBacktickRun(string value)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in value)
        {
            current = character == '`' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static bool TryGet(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value)) return true;
        value = default;
        return false;
    }

    private static string RawJson(JsonElement value)
    {
        try
        {
            return value.GetRawText();
        }
        catch (InvalidOperationException)
        {
            return "<unavailable JSON value>";
        }
    }

    private static string CompactJson(JsonElement value) => JsonSerializer.Serialize(value);

    private static string InlineCode(string value)
    {
        var delimiter = new string('`', Math.Max(1, LongestBacktickRun(value) + 1));
        var padding = value.StartsWith('`') || value.EndsWith('`') || value.StartsWith(' ') || value.EndsWith(' ')
            ? " "
            : "";
        return delimiter + padding + value + padding + delimiter;
    }

    private static string EscapeTable(string value) =>
        EscapeMarkdown(value).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string EscapeMarkdown(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '#' or '+' or '|'
                or '!')
                result.Append('\\');
            if (character is '\r' or '\n')
                result.Append(' ');
            else
                result.Append(character);
        }

        return result.ToString();
    }
}
