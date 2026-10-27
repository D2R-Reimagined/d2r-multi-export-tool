// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Shared safe formatting for Markdown comparison reports.</summary>
internal static class ComparisonMarkdown
{
    private const string PerLevelSuffixKey = "strPerCharacterLevelSuffix";

    public static void AppendSnapshotSummary(StringBuilder markdown, string label, SnapshotInfo snapshot)
    {
        markdown.Append("- ").Append(label).Append(" snapshot: ").Append(InlineCode(snapshot.Path))
            .Append("; manifest schema ")
            .Append(snapshot.ManifestSchemaVersion?.ToString() ?? "unavailable").Append("; Succeeded=")
            .Append(snapshot.Succeeded?.ToString() ?? "unavailable").Append("; Complete=")
            .Append(snapshot.Complete?.ToString() ?? "unavailable").Append("; manifest SHA-256 ")
            .Append(InlineCode(snapshot.ManifestSha256 ?? "unavailable")).AppendLine(".");
    }

    public static void AppendIssues(StringBuilder markdown, IReadOnlyCollection<ComparisonIssue> issues,
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

    public static void AppendNestedKeyedLines(
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

    public static string RenderValue(JsonElement value, RecipeTranslations translations) =>
        value.ValueKind == JsonValueKind.Object
            ? RenderKeyedLine(value, translations)
            : EscapeMarkdown(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : RawJson(value));

    public static string RenderKeyedLine(JsonElement line, RecipeTranslations translations)
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

    internal static bool TryFormatTemplate(
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
            && args.All(static value => value.ValueKind == JsonValueKind.Number))
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
                if (argument.ValueKind != JsonValueKind.Number)
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

    public static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static void AppendDetailsStart(StringBuilder markdown, string summaryText)
    {
        markdown.AppendLine("<details>");
        markdown.Append("<summary>").Append(SummaryText(summaryText)).AppendLine("</summary>");
        markdown.AppendLine();
    }

    public static void AppendDetailsEnd(StringBuilder markdown)
    {
        markdown.AppendLine("</details>");
        markdown.AppendLine();
    }

    public static string SummaryText(string markdownText)
    {
        var text = new StringBuilder(markdownText.Length);
        for (var index = 0; index < markdownText.Length; index++)
        {
            if (markdownText[index] == '\\' && index + 1 < markdownText.Length &&
                markdownText[index + 1] is '\\' or '`' or '*' or '_' or '{' or '}' or '[' or ']' or '<' or '>' or '#' or
                    '+' or '|' or '!')
            {
                text.Append(markdownText[++index]);
                continue;
            }

            if (markdownText[index] == '`') continue;
            if (markdownText[index] == '*' && index + 1 < markdownText.Length && markdownText[index + 1] == '*')
            {
                index++;
                continue;
            }

            text.Append(markdownText[index]);
        }

        return text.ToString().Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    public static void AppendRawRecord(StringBuilder markdown, JsonElement record,
        string summary = "Complete original record JSON")
    {
        var json = RawJson(record);
        var fenceLength = Math.Max(3, LongestBacktickRun(json) + 1);
        var fence = new string('`', fenceLength);
        markdown.AppendLine("<details>");
        markdown.Append("<summary>").Append(EscapeMarkdown(summary)).AppendLine("</summary>");
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

    public static bool TryGet(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out value)) return true;
        value = default;
        return false;
    }

    public static string RawJson(JsonElement value)
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

    public static string CompactJson(JsonElement value) => JsonSerializer.Serialize(value);

    public static string InlineCode(string value)
    {
        var delimiter = new string('`', Math.Max(1, LongestBacktickRun(value) + 1));
        var padding = value.StartsWith('`') || value.EndsWith('`') || value.StartsWith(' ') || value.EndsWith(' ')
            ? " "
            : "";
        return delimiter + padding + value + padding + delimiter;
    }

    public static string EscapeTable(string value) =>
        EscapeMarkdown(value).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    public static string EscapeMarkdown(string value)
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
