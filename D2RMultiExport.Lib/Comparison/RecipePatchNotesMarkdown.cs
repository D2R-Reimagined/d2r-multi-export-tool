// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Renders player-facing crafting changes from a verified snapshot comparison.</summary>
internal static class RecipePatchNotesMarkdown
{
    private const string CubeRecipeCategory = "cube-recipes";
    private const string OtherRecipesCategory = "Other recipes";
    private static readonly string[] OutputSlots = ["A", "B", "C"];

    public static async Task<string> RenderAsync(ComparisonReport report)
    {
        var oldTranslations = await RecipeTranslations.LoadAsync(report.OldSnapshot);
        var newTranslations = await RecipeTranslations.LoadAsync(report.NewSnapshot);
        var context = new RenderContext();
        var category = report.Categories.FirstOrDefault(item => item.Category == CubeRecipeCategory);
        var entries = category?.Status == "Compared"
            ? BuildEntries(category, oldTranslations, newTranslations, context)
            : [];
        var sections = new StringBuilder();
        if (category?.Status == "Compared")
            AppendRecipeSections(sections, entries, oldTranslations, newTranslations, context);

        var markdown = new StringBuilder();
        markdown.AppendLine("# Recipe patch notes");
        markdown.AppendLine();
        AppendSummary(markdown, report, category, entries, oldTranslations, newTranslations);
        markdown.AppendLine("## Recipe identity and unmatched definitions");
        markdown.AppendLine();
        markdown.AppendLine(
            "Recipe ingredients and technical conditions contribute to recipe identity, so a change can appear as an unmatched removal and addition. These entries are not automatically paired or described as confirmed additions or deletions; recipe restructuring is possible. Technical conditions are not interpreted here; see the detailed comparison.");
        markdown.AppendLine();

        if (category?.Status != "Compared")
        {
            markdown.Append("## Recipe changes unavailable").AppendLine();
            markdown.AppendLine();
            markdown.AppendLine("The recipe category is unavailable; no change counts are inferred.");
            markdown.AppendLine();
        }
        else if (entries.Count == 0)
        {
            markdown.AppendLine("## Recipe changes");
            markdown.AppendLine();
            markdown.AppendLine("*No recipe changes were reported.*");
            markdown.AppendLine();
        }
        else
            markdown.Append(sections);

        return markdown.ToString();
    }

    private static List<RecipeEntry> BuildEntries(
        CategoryComparison category,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var entries = new List<RecipeEntry>(category.Counts.Added + category.Counts.Removed +
                                            category.Counts.Modified);
        foreach (var record in category.ModifiedRecords)
            entries.Add(CreateEntry(RecipeChangeKind.Modified, record.Key, record.NewRowIndex, record.New,
                record.Old, record.New, newTranslations, context));
        foreach (var record in category.AddedRecords)
            entries.Add(CreateEntry(RecipeChangeKind.Added, record.Key, record.RowIndex, record.Record,
                default, record.Record, newTranslations, context));
        foreach (var record in category.RemovedRecords)
            entries.Add(CreateEntry(RecipeChangeKind.Removed, record.Key, record.RowIndex, record.Record,
                record.Record, default, oldTranslations, context));

        return entries;
    }

    private static RecipeEntry CreateEntry(
        RecipeChangeKind kind,
        string key,
        int? rowIndex,
        JsonElement record,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations translations,
        RenderContext context)
    {
        var category = GetRecipeCategory(record, translations, context);
        var title = GetRecipeTitle(record, translations, context);
        if (TryGet(record, "RequiredClass", out var requiredClass) &&
            requiredClass.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(requiredClass.GetString()))
            title = RenderValue(requiredClass, translations, context) + "-only recipe: " + title;
        return new RecipeEntry(kind, key, rowIndex, record, oldRecord, newRecord, category, title);
    }

    private static string GetRecipeCategory(
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (!TryGet(record, "Notes", out var notes) || notes.ValueKind != JsonValueKind.Array ||
            notes.GetArrayLength() == 0)
            return OtherRecipesCategory;

        var before = context.PropertyState.UntranslatedValues;
        var primaryNote = RenderValue(notes[0], translations, context);
        return context.PropertyState.UntranslatedValues == before &&
               !string.IsNullOrWhiteSpace(primaryNote)
            ? primaryNote
            : OtherRecipesCategory;
    }

    private static string GetRecipeTitle(JsonElement record, RecipeTranslations translations, RenderContext context)
    {
        if (TryGet(record, "Description", out var description) && !IsEmptyValue(description))
            return RenderValue(description, translations, context);

        var inputs = TryGet(record, "Inputs", out var inputValues) && inputValues.ValueKind == JsonValueKind.Array
            ? inputValues.EnumerateArray().Select(input => RenderIngredient(input, translations, context))
            : [];
        var outputs = TryGet(record, "Outputs", out var outputValues)
            ? OutputSlots.Where(slot => TryGet(outputValues, slot, out _))
                .Select(slot => TryGet(outputValues, slot, out var output)
                    ? RenderOutputName(output, translations, context, GetArray(record, "Inputs"))
                    : "")
            : [];
        var inputText = string.Join(" + ", inputs.Where(static value => value.Length > 0));
        var outputText = string.Join(" / ", outputs.Where(static value => value.Length > 0));
        if (inputText.Length > 0 && outputText.Length > 0) return inputText + " → " + outputText;
        if (inputText.Length > 0) return inputText + " → crafting result";
        if (outputText.Length > 0) return "Recipe for " + outputText;
        return "Crafting recipe";
    }

    private static void AppendSummary(
        StringBuilder markdown,
        ComparisonReport report,
        CategoryComparison? category,
        IReadOnlyCollection<RecipeEntry> entries,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations)
    {
        markdown.AppendLine("## Summary");
        markdown.AppendLine();

        if (category?.Status == "Compared")
        {
            markdown.AppendLine(
                "| Crafting category | Modified | Added | Removed |");
            markdown.AppendLine("|---|---:|---:|---:|");
            foreach (var group in entries.GroupBy(static entry => entry.Category, StringComparer.Ordinal)
                         .OrderBy(static group => group.Key == OtherRecipesCategory ? 1 : 0)
                         .ThenBy(static group => group.Key, StringComparer.Ordinal))
            {
                markdown.Append("| ").Append(ComparisonMarkdown.EscapeMarkdown(group.Key)).Append(" | ")
                    .Append(group.Count(static entry => entry.Kind == RecipeChangeKind.Modified)
                        .ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(group.Count(static entry => entry.Kind == RecipeChangeKind.Added)
                        .ToString(CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(group.Count(static entry => entry.Kind == RecipeChangeKind.Removed)
                        .ToString(CultureInfo.InvariantCulture)).AppendLine(" |");
            }

            if (entries.Count == 0) markdown.AppendLine("| *No affected categories* | 0 | 0 | 0 |");
        }
        else
        {
            markdown.AppendLine(
                "Recipe change counts are unavailable because the cube-recipe category was not compared.");
        }

        if (oldTranslations.Status.StartsWith("unavailable", StringComparison.Ordinal) ||
            newTranslations.Status.StartsWith("unavailable", StringComparison.Ordinal))
            markdown.AppendLine("Some English labels could not be verified; see the detailed comparison.");
        if (report.OldSnapshot.Errors.Count > 0 || report.NewSnapshot.Errors.Count > 0 ||
            category?.Errors.Count > 0)
            markdown.AppendLine("Some comparison details are unavailable; see the detailed comparison.");

        markdown.AppendLine();
    }

    private static void AppendRecipeSections(
        StringBuilder markdown,
        IReadOnlyCollection<RecipeEntry> entries,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        markdown.AppendLine("## Recipe changes");
        markdown.AppendLine();
        foreach (var categoryGroup in entries.GroupBy(static entry => entry.Category, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key == OtherRecipesCategory ? 1 : 0)
                     .ThenBy(static group => group.Key, StringComparer.Ordinal))
        {
            var modifiedCount = categoryGroup.Count(static entry => entry.Kind == RecipeChangeKind.Modified);
            var addedCount = categoryGroup.Count(static entry => entry.Kind == RecipeChangeKind.Added);
            var removedCount = categoryGroup.Count(static entry => entry.Kind == RecipeChangeKind.Removed);
            ComparisonMarkdown.AppendDetailsStart(markdown,
                $"{categoryGroup.Key} — {ComparisonMarkdown.Invariant(modifiedCount)} modified, " +
                $"{ComparisonMarkdown.Invariant(addedCount)} added, {ComparisonMarkdown.Invariant(removedCount)} removed");
            AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Modified, "Confirmed matched modifications",
                oldTranslations, newTranslations, context);
            AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Added, "Added recipe definitions",
                oldTranslations, newTranslations, context);
            AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Removed, "Removed recipe definitions",
                oldTranslations, newTranslations, context);
            ComparisonMarkdown.AppendDetailsEnd(markdown);
        }
    }

    private static void AppendEntrySection(
        StringBuilder markdown,
        IEnumerable<RecipeEntry> entries,
        RecipeChangeKind kind,
        string heading,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var selected = entries.Where(entry => entry.Kind == kind)
            .OrderBy(static entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0) return;

        markdown.Append("### ").Append(heading).Append(" (")
            .Append(ComparisonMarkdown.Invariant(selected.Length)).AppendLine(")").AppendLine();
        var duplicateTitles = selected.GroupBy(static entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in selected)
        {
            markdown.Append("#### ").AppendLine(entry.Title).AppendLine();
            AppendTrace(markdown, entry.Key);
            if (duplicateTitles.Contains(entry.Title) && HasTechnicalConditions(entry.Record) &&
                (kind != RecipeChangeKind.Modified ||
                 !HasTechnicalConditionChanges(entry.OldRecord, entry.NewRecord)))
                markdown.AppendLine("- Recipe condition distinguishes this definition; see detailed comparison.");
            AppendAssignedNotes(markdown, entry.Record, entry.Category,
                kind == RecipeChangeKind.Removed ? oldTranslations : newTranslations, context);
            if (kind == RecipeChangeKind.Modified)
                AppendModifiedRecipe(markdown, entry, oldTranslations, newTranslations, context);
            else
                AppendRecipeDefinition(markdown, entry.Record,
                    kind == RecipeChangeKind.Added ? newTranslations : oldTranslations, context);
            markdown.AppendLine();
        }
    }

    private static void AppendRecipeDefinition(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (!TryGet(record, "RequiredClass", out var requiredClass) || IsEmptyValue(requiredClass))
            AppendDefinitionField(markdown, record, "Class", "Class restriction", translations, context);
        var showInstructions = TryGet(record, "Description", out var description) && !IsEmptyValue(description);
        if (showInstructions) AppendInputs(markdown, record, translations, context);
        AppendOutputs(markdown, record, translations, context, showInstructions);

        if (TryGet(record, "Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            markdown.AppendLine("- This recipe is disabled.");

        AppendUnknownRootFields(markdown, record, context, null);
    }

    private static void AppendModifiedRecipe(
        StringBuilder markdown,
        RecipeEntry entry,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var oldRecord = entry.OldRecord;
        var newRecord = entry.NewRecord;
        var changesBefore = markdown.Length;
        AppendMetadataChange(markdown, oldRecord, newRecord, "Description", "Recipe description changed",
            oldTranslations,
            newTranslations, context);
        AppendMetadataChange(markdown, oldRecord, newRecord, "Class", "Class restriction changed", oldTranslations,
            newTranslations, context);
        AppendMetadataChange(markdown, oldRecord, newRecord, "RequiredClass", "Required class restriction changed",
            oldTranslations, newTranslations, context);
        AppendNotesChange(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendEnabledChange(markdown, oldRecord, newRecord, context);
        AppendInputChanges(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendOutputChanges(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendOperationReview(markdown, oldRecord, newRecord);
        AppendUnknownRootFields(markdown, newRecord, context, oldRecord);

        if (markdown.Length == changesBefore)
            AddReview(markdown, context,
                "Some recipe changes cannot be described safely; see detailed comparison.");
    }

    private static void AppendDefinitionField(
        StringBuilder markdown,
        JsonElement record,
        string field,
        string label,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (!TryGet(record, field, out var value) || IsEmptyValue(value)) return;
        markdown.Append("- ").Append(label).Append(": ")
            .AppendLine(RenderValue(value, translations, context));
    }

    private static void AppendAssignedNotes(
        StringBuilder markdown,
        JsonElement record,
        string category,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (!TryGet(record, "Notes", out var notes) || notes.ValueKind != JsonValueKind.Array ||
            notes.GetArrayLength() == 0) return;

        var rendered = notes.EnumerateArray().Select(note => RenderValue(note, translations, context)).ToArray();
        var labels = category == OtherRecipesCategory ? rendered : rendered.Skip(1).ToArray();
        if (labels.Length == 0) return;

        markdown.Append(category == OtherRecipesCategory ? "- Assigned notes: " : "- Additional notes: ")
            .AppendLine(string.Join(" / ", labels));
    }

    private static void AppendInputs(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        markdown.AppendLine("- Ingredients:");
        if (!TryGet(record, "Inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
        {
            AddReview(markdown, context, "Ingredient data is missing or has an unsupported shape.", 1);
            return;
        }

        if (!inputs.EnumerateArray().Any())
        {
            markdown.AppendLine("  - None listed.");
            return;
        }

        foreach (var input in inputs.EnumerateArray())
        {
            markdown.Append("  - ").AppendLine(RenderIngredient(input, translations, context));
        }
    }

    private static string RenderIngredient(JsonElement input, RecipeTranslations translations, RenderContext context)
    {
        var name = RenderQualifiedItemName(input, translations, context);
        if (!TryGet(input, "Quantity", out var quantity)) return name;
        if (TryGetInt(quantity, out var numericQuantity) && numericQuantity == 1) return name;
        return RenderValue(quantity, translations, context) + " × " + name;
    }

    private static void AppendOutputs(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context,
        bool showResultNames)
    {
        if (!TryGet(record, "Outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
        {
            AddReview(markdown, context, "Result data is missing or has an unsupported shape.", 1);
            return;
        }

        var slots = OutputSlots.Where(slot => TryGet(outputs, slot, out _)).ToArray();
        var hasResultHeading = showResultNames || slots.Length > 1;
        if (hasResultHeading) markdown.AppendLine("- Results:");
        foreach (var slot in slots)
        {
            TryGet(outputs, slot, out var output);
            AppendOutputDefinition(markdown, output, translations, context, hasResultHeading ? 1 : 0,
                showResultNames, slots.Length > 1, GetArray(record, "Inputs"));
        }

        if (slots.Length == 0) markdown.AppendLine("- No results listed.");
        foreach (var property in outputs.EnumerateObject().Where(property => !OutputSlots.Contains(property.Name)))
            AddReview(markdown, context, "Some result details are not available in the player-facing report.", 1);
    }

    private static void AppendOutputDefinition(
        StringBuilder markdown,
        JsonElement output,
        RecipeTranslations translations,
        RenderContext context,
        int depth,
        bool showResultName,
        bool multipleOutputs,
        IReadOnlyList<JsonElement> inputs)
    {
        var indent = new string(' ', depth * 2);
        if (output.ValueKind != JsonValueKind.Object)
        {
            AddReview(markdown, context, "A result could not be displayed because its data is incomplete.", depth);
            return;
        }

        if (showResultName || multipleOutputs)
            markdown.Append(indent).Append("- ")
                .AppendLine(RenderOutputName(output, translations, context, inputs));
        var nestedIndent = new string(' ', (showResultName || multipleOutputs ? depth + 1 : depth) * 2);
        if (TryGet(output, "OutputChance", out var chance))
            markdown.Append(nestedIndent).Append("- Result chance: ")
                .AppendLine(RenderValue(chance, translations, context));
        if (TryGet(output, "Lines", out var lines))
            AppendPropertyLines(markdown, lines, translations, context,
                depth + (showResultName || multipleOutputs ? 1 : 0));
        AppendUnknownOutputFields(markdown, output, context, depth + 1);
    }

    private static string RenderOutputName(
        JsonElement output,
        RecipeTranslations translations,
        RenderContext context,
        IReadOnlyList<JsonElement> inputs,
        bool includeQuantity = true)
    {
        var nameKey = TryGet(output, "Name", out var nameValue) ? GetKey(nameValue) : null;
        string name;
        if (nameKey == SyntheticStringRegistry.Keys.CubeOutputUseItemFromInput1)
        {
            var originalName = inputs.Count > 0
                ? RenderQualifiedItemName(inputs[0], translations, context)
                : "input item";
            originalName = originalName.StartsWith("Any ", StringComparison.OrdinalIgnoreCase)
                ? originalName[4..]
                : originalName;
            name = RenderQualifiers("Original " + originalName, output, translations, context);
        }
        else if (nameKey == SyntheticStringRegistry.Keys.CubeOutputUseTypeOfInput1)
        {
            var baseName = inputs.Count > 0
                ? RenderItemName(inputs[0], translations, context)
                : "input item";
            baseName = baseName.StartsWith("Any ", StringComparison.OrdinalIgnoreCase) ? baseName[4..] : baseName;
            name = "New " + RenderQualifiers(baseName, output, translations, context) + " (same base type)";
        }
        else
        {
            var baseName = TryGet(output, "Name", out _)
                ? RenderItemName(output, translations, context)
                : "result item";
            name = RenderQualifiers(baseName, output, translations, context);
        }

        if (!includeQuantity || !TryGet(output, "Quantity", out var quantity)) return name;
        if (TryGetInt(quantity, out var numericQuantity) && numericQuantity == 1) return name;
        return RenderValue(quantity, translations, context) + " × " + name;
    }

    private static string RenderQualifiedItemName(
        JsonElement item,
        RecipeTranslations translations,
        RenderContext context) =>
        RenderQualifiers(RenderItemName(item, translations, context), item, translations, context);

    private static string RenderItemName(JsonElement item, RecipeTranslations translations, RenderContext context)
    {
        if (!TryGet(item, "Name", out var name)) return "item";
        var rendered = RenderValue(name, translations, context);
        return rendered == "Translation unavailable; see detailed comparison." ? "Untranslated item" : rendered;
    }

    private static string RenderQualifiers(
        string itemName,
        JsonElement item,
        RecipeTranslations translations,
        RenderContext context)
    {
        var adjectives = new List<string>();
        var otherQualifiers = new List<string>();
        foreach (var qualifier in GetQualifiers(item))
        {
            if (TryRenderConditionQualifier(qualifier, out var conditionQualifier))
            {
                otherQualifiers.Add(conditionQualifier);
                continue;
            }

            if (TryRenderSocketRequirement(qualifier, out var socketRequirement))
            {
                otherQualifiers.Add(socketRequirement);
                continue;
            }

            var text = RenderValue(qualifier, translations, context);
            var adjective = GetItemAdjective(text);
            if (adjective is not null)
                adjectives.Add(adjective);
            else if (!string.IsNullOrWhiteSpace(text))
                otherQualifiers.Add(text);
        }

        if (adjectives.Count > 0 && itemName.StartsWith("Any ", StringComparison.OrdinalIgnoreCase))
            itemName = itemName[4..];
        if (adjectives.Count > 0) itemName = string.Join(" ", adjectives) + " " + itemName;
        if (otherQualifiers.Count > 0) itemName += " (" + string.Join("; ", otherQualifiers) + ")";
        return itemName;
    }

    private static string? GetItemAdjective(string qualifier)
    {
        var normalized = qualifier.Trim();
        if (normalized.Equals("Not Ethereal", StringComparison.OrdinalIgnoreCase)) return "Non-Ethereal";
        if (normalized.Equals("High Quality (Superior)", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("High Quality Item (Superior)", StringComparison.OrdinalIgnoreCase)) return "Superior";
        foreach (var adjective in new[]
                 {
                     "Rare", "Unique", "Magic", "Set", "Crafted", "Tempered", "Low Quality", "Normal Quality",
                     "Superior", "Non-Ethereal", "Ethereal", "Basic", "Exceptional", "Elite", "Socketed"
                 })
        {
            if (normalized.Equals(adjective, StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals(adjective + " Item", StringComparison.OrdinalIgnoreCase)) return adjective;
        }

        if (normalized.Equals("Normal Item", StringComparison.OrdinalIgnoreCase)) return "Normal Quality";
        if (normalized.Equals("Low Quality Item", StringComparison.OrdinalIgnoreCase)) return "Low Quality";
        return null;
    }

    private static bool TryRenderSocketRequirement(JsonElement qualifier, out string requirement)
    {
        requirement = "";
        if (!TryGet(qualifier, "key", out var key) || key.ValueKind != JsonValueKind.String ||
            key.GetString() is not (SyntheticStringRegistry.Keys.CubeQualifierItemWithSockets or
                SyntheticStringRegistry.Keys.CubeQualifierItemWithSocketsN)) return false;

        if (TryGet(qualifier, "args", out var args) && args.ValueKind == JsonValueKind.Array &&
            args.GetArrayLength() > 0 && TryGetInt(args[0], out var count))
            requirement = count.ToString(CultureInfo.InvariantCulture) + " sockets";
        else
            requirement = "with sockets";
        return true;
    }

    private static string? GetKey(JsonElement value)
    {
        if (TryGet(value, "key", out var key) && key.ValueKind == JsonValueKind.String) return key.GetString();
        return TryGet(value, "nameKey", out var nameKey) && nameKey.ValueKind == JsonValueKind.String
            ? nameKey.GetString()
            : null;
    }

    private static void AppendQualifiers(
        StringBuilder markdown,
        JsonElement record,
        string label,
        RecipeTranslations translations,
        RenderContext context,
        int depth)
    {
        if (!TryGet(record, "Qualifiers", out var qualifiers)) return;
        var indent = new string(' ', depth * 2);
        if (qualifiers.ValueKind != JsonValueKind.Array)
        {
            AddReview(markdown, context, $"{label} data has an unsupported shape.", depth);
            return;
        }

        var hasQuantity = TryGet(record, "Quantity", out _);
        foreach (var qualifier in qualifiers.EnumerateArray())
        {
            if (hasQuantity && IsQuantityQualifier(qualifier)) continue;
            markdown.Append(indent).Append("- ").Append(label).Append(": ")
                .AppendLine(RenderValue(qualifier, translations, context));
        }
    }

    private static void AppendPropertyLines(
        StringBuilder markdown,
        JsonElement lines,
        RecipeTranslations translations,
        RenderContext context,
        int depth)
    {
        if (lines.ValueKind != JsonValueKind.Array)
        {
            AddReview(markdown, context, "Output property data has an unsupported shape.", depth);
            return;
        }

        var hasUntranslated = false;
        foreach (var line in lines.EnumerateArray())
        {
            var lineBefore = context.PropertyState.UntranslatedValues;
            var rendered = ItemPatchNotesMarkdown.RenderPropertyLineText(line, translations, context.PropertyState);
            if (context.PropertyState.UntranslatedValues > lineBefore &&
                rendered.Contains("Property description unavailable; see detailed comparison.",
                    StringComparison.Ordinal))
            {
                hasUntranslated = true;
                continue;
            }

            markdown.Append(new string(' ', depth * 2)).Append("- ").AppendLine(rendered);
        }

        if (hasUntranslated)
            markdown.Append(new string(' ', depth * 2))
                .AppendLine("- Note: Some result properties cannot be translated safely; see detailed comparison.");
    }

    private static void AppendInputChanges(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var oldInputs = GetArray(oldRecord, "Inputs");
        var newInputs = GetArray(newRecord, "Inputs");
        var oldMatched = new bool[oldInputs.Count];
        var newMatched = new bool[newInputs.Count];
        MatchExactInputs(oldInputs, newInputs, oldMatched, newMatched);
        var oldByName = GroupUnmatchedByName(oldInputs, oldMatched);
        var newByName = GroupUnmatchedByName(newInputs, newMatched);

        foreach (var pair in oldByName.OrderBy(static pair => pair.Value[0]))
        {
            if (pair.Value.Count != 1 || !newByName.TryGetValue(pair.Key, out var newIndexes) ||
                newIndexes.Count != 1) continue;
            var oldIndex = pair.Value[0];
            var newIndex = newIndexes[0];
            oldMatched[oldIndex] = true;
            newMatched[newIndex] = true;
            AppendIngredientChange(markdown, oldInputs[oldIndex], newInputs[newIndex], oldTranslations,
                newTranslations, context);
        }

        foreach (var index in Enumerable.Range(0, newInputs.Count).Where(index => !newMatched[index]))
            markdown.Append("- Added ingredient: ")
                .AppendLine(RenderIngredient(newInputs[index], newTranslations, context));
        foreach (var index in Enumerable.Range(0, oldInputs.Count).Where(index => !oldMatched[index]))
            markdown.Append("- Removed ingredient: ")
                .AppendLine(RenderIngredient(oldInputs[index], oldTranslations, context));

        var oldOrder = oldInputs.Select(InputSignature).ToArray();
        var newOrder = newInputs.Select(InputSignature).ToArray();
        if (oldOrder.Length == newOrder.Length &&
            oldOrder.Order(StringComparer.Ordinal)
                .SequenceEqual(newOrder.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
            !oldOrder.SequenceEqual(newOrder, StringComparer.Ordinal))
            markdown.AppendLine("- Recipe condition changed; see detailed comparison.");
    }

    private static void MatchExactInputs(IReadOnlyList<JsonElement> oldInputs, IReadOnlyList<JsonElement> newInputs,
        bool[] oldMatched, bool[] newMatched)
    {
        var newBySignature = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < newInputs.Count; index++)
        {
            var signature = InputSignature(newInputs[index]);
            if (!newBySignature.TryGetValue(signature, out var indexes))
                newBySignature.Add(signature, indexes = new Queue<int>());
            indexes.Enqueue(index);
        }

        for (var index = 0; index < oldInputs.Count; index++)
        {
            if (!newBySignature.TryGetValue(InputSignature(oldInputs[index]), out var indexes) || indexes.Count == 0)
                continue;
            oldMatched[index] = true;
            newMatched[indexes.Dequeue()] = true;
        }
    }

    private static Dictionary<string, List<int>> GroupUnmatchedByName(IReadOnlyList<JsonElement> inputs, bool[] matched)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < inputs.Count; index++)
        {
            if (matched[index]) continue;
            var name = TryGet(inputs[index], "Name", out var value) ? IngredientNameIdentity(value) : "";
            if (!groups.TryGetValue(name, out var indexes)) groups.Add(name, indexes = []);
            indexes.Add(index);
        }

        return groups;
    }

    private static string InputSignature(JsonElement input)
    {
        var name = TryGet(input, "Name", out var nameValue) ? ItemPatchNotesMarkdown.CanonicalJson(nameValue) : "";
        var quantity = TryGet(input, "Quantity", out var quantityValue)
            ? ItemPatchNotesMarkdown.CanonicalJson(quantityValue)
            : "?";
        var qualifiers = string.Join(";", GetQualifiers(input)
            .Select(ItemPatchNotesMarkdown.CanonicalJson).Order(StringComparer.Ordinal));
        return JsonSerializer.Serialize(new { name, quantity, qualifiers });
    }

    private static string IngredientNameIdentity(JsonElement name) =>
        TryGet(name, "key", out var key) && key.ValueKind == JsonValueKind.String
            ? key.GetString() ?? ""
            : TryGet(name, "nameKey", out var nameKey) && nameKey.ValueKind == JsonValueKind.String
                ? nameKey.GetString() ?? ""
                : ItemPatchNotesMarkdown.CanonicalJson(name);

    private static void AppendIngredientChange(
        StringBuilder markdown,
        JsonElement oldInput,
        JsonElement newInput,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var oldName = TryGet(oldInput, "Name", out _)
            ? RenderItemName(oldInput, oldTranslations, context)
            : "<missing ingredient name>";
        var newName = TryGet(newInput, "Name", out _)
            ? RenderItemName(newInput, newTranslations, context)
            : "<missing ingredient name>";
        var oldQuantity = TryGet(oldInput, "Quantity", out var oldQuantityValue)
            ? RenderValue(oldQuantityValue, oldTranslations, context)
            : "?";
        var newQuantity = TryGet(newInput, "Quantity", out var newQuantityValue)
            ? RenderValue(newQuantityValue, newTranslations, context)
            : "?";
        if (oldName != newName)
            markdown.Append("- Required ingredient: ").Append(newName).Append(" (was ").Append(oldName)
                .AppendLine(").");
        else if (!SameField(oldInput, newInput, "Name"))
            markdown.Append("- Ingredient changed (still shown as ").Append(newName)
                .AppendLine("); see detailed comparison.");
        if (oldQuantity != newQuantity)
            markdown.Append("- Ingredient quantity: ").Append(FormatQuantity(newQuantity, newName))
                .Append(" (was ").Append(FormatQuantity(oldQuantity, oldName)).AppendLine(").");
        AppendQualifierChanges(markdown, oldInput, newInput, oldTranslations, newTranslations, context,
            "ingredient " + newName);
    }

    private static void AppendOutputChanges(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        if (!TryGet(oldRecord, "Outputs", out var oldOutputs) || oldOutputs.ValueKind != JsonValueKind.Object ||
            !TryGet(newRecord, "Outputs", out var newOutputs) || newOutputs.ValueKind != JsonValueKind.Object)
        {
            if (!SameField(oldRecord, newRecord, "Outputs"))
                AddReview(markdown, context, "Recipe result structure changed; review the result data.");
            return;
        }

        var oldInputs = GetArray(oldRecord, "Inputs");
        var newInputs = GetArray(newRecord, "Inputs");
        var multipleResults = OutputSlots.Count(slot => TryGet(oldOutputs, slot, out _)) > 1 ||
                              OutputSlots.Count(slot => TryGet(newOutputs, slot, out _)) > 1;
        foreach (var slot in OutputSlots)
        {
            var hasOld = TryGet(oldOutputs, slot, out var oldOutput);
            var hasNew = TryGet(newOutputs, slot, out var newOutput);
            if (!hasOld && hasNew)
            {
                markdown.Append("- Added result: ")
                    .AppendLine(RenderOutputName(newOutput, newTranslations, context, newInputs));
                AppendOutputDefinition(markdown, newOutput, newTranslations, context, 1,
                    false, false, newInputs);
                continue;
            }

            if (hasOld && !hasNew)
            {
                markdown.Append("- Removed result: ")
                    .AppendLine(RenderOutputName(oldOutput, oldTranslations, context, oldInputs));
                AppendOutputDefinition(markdown, oldOutput, oldTranslations, context, 1,
                    false, false, oldInputs);
                continue;
            }

            if (hasOld && hasNew)
                AppendOutputChange(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context,
                    oldInputs, newInputs, multipleResults);
        }

        foreach (var property in oldOutputs.EnumerateObject().Where(property => !OutputSlots.Contains(property.Name)))
            AddReview(markdown, context, "Some result details are no longer available; see detailed comparison.");
        foreach (var property in newOutputs.EnumerateObject().Where(property => !OutputSlots.Contains(property.Name)))
        {
            if (!TryGet(oldOutputs, property.Name, out var oldValue) ||
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) != ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                AddReview(markdown, context,
                    "Some result details changed in a way that cannot be described safely; see detailed comparison.");
        }
    }

    private static void AppendOutputChange(
        StringBuilder markdown,
        JsonElement oldOutput,
        JsonElement newOutput,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context,
        IReadOnlyList<JsonElement> oldInputs,
        IReadOnlyList<JsonElement> newInputs,
        bool multipleResults)
    {
        if (oldOutput.ValueKind != JsonValueKind.Object || newOutput.ValueKind != JsonValueKind.Object)
        {
            if (ItemPatchNotesMarkdown.CanonicalJson(oldOutput) != ItemPatchNotesMarkdown.CanonicalJson(newOutput))
                AddReview(markdown, context, "A result changed in a way that cannot be described safely.");
            return;
        }

        var oldName = TryGet(oldOutput, "Name", out _)
            ? RenderOutputName(oldOutput, oldTranslations, context, oldInputs, includeQuantity: false)
            : "<missing result name>";
        var newName = TryGet(newOutput, "Name", out _)
            ? RenderOutputName(newOutput, newTranslations, context, newInputs, includeQuantity: false)
            : "<missing result name>";
        if (oldName != newName)
            markdown.Append("- Result: ").Append(newName).Append(" (was ").Append(oldName).AppendLine(").");
        else if (!SameField(oldOutput, newOutput, "Name"))
            markdown.Append("- Result item changed (still shown as ").Append(newName)
                .AppendLine("); see detailed comparison.");

        var oldQuantity = TryGet(oldOutput, "Quantity", out var oldQuantityValue)
            ? RenderValue(oldQuantityValue, oldTranslations, context)
            : "?";
        var newQuantity = TryGet(newOutput, "Quantity", out var newQuantityValue)
            ? RenderValue(newQuantityValue, newTranslations, context)
            : "?";
        if (oldQuantity != newQuantity)
            markdown.Append("- Result quantity: ").Append(FormatQuantity(newQuantity, newName)).Append(" (was ")
                .Append(FormatQuantity(oldQuantity, oldName)).AppendLine(").");

        AppendQualifierChanges(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context,
            "result requirement");
        AppendValueChange(markdown, oldOutput, newOutput, "OutputChance", "Result chance",
            oldTranslations, newTranslations, context);
        AppendOutputPropertyChanges(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context,
            newName, multipleResults);
        AppendUnknownOutputFields(markdown, newOutput, context, 0, oldOutput);
    }

    private static string FormatQuantity(string quantity, string itemName) =>
        quantity == "1" ? itemName : quantity + " × " + itemName;

    private static void AppendOutputPropertyChanges(
        StringBuilder markdown,
        JsonElement oldOutput,
        JsonElement newOutput,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context,
        string resultName,
        bool multipleResults)
    {
        var oldLines = GetArray(oldOutput, "Lines");
        var newLines = GetArray(newOutput, "Lines");
        var diff = ItemPatchNotesMarkdown.DiffProperties(oldLines, newLines, oldTranslations, newTranslations,
            context.PropertyState);
        var hasUnsafeFallback = false;
        foreach (var change in diff.Changes)
        {
            if (change == "Property order changed.")
            {
                markdown.AppendLine("- Recipe condition changed; see detailed comparison.");
                continue;
            }

            if (change.Contains("Property description unavailable; see detailed comparison.",
                    StringComparison.Ordinal))
            {
                hasUnsafeFallback = true;
                continue;
            }

            var resultPrefix = multipleResults ? resultName + " — " : "";
            if (change.StartsWith("Changed: ", StringComparison.Ordinal))
                markdown.Append("- Changed: ").Append(resultPrefix).AppendLine(change[9..]);
            else if (change.StartsWith("Added: ", StringComparison.Ordinal))
                markdown.Append("- Added: ").Append(resultPrefix).AppendLine(change[7..]);
            else if (change.StartsWith("Removed: ", StringComparison.Ordinal))
                markdown.Append("- Removed: ").Append(resultPrefix).AppendLine(change[9..]);
            else
                AddReview(markdown, context,
                    "A result property changed in a way that cannot be described safely; see detailed comparison.");
        }

        if (hasUnsafeFallback)
            markdown.AppendLine("- Note: Some result properties cannot be translated safely; see detailed comparison.");
    }

    private static void AppendQualifierChanges(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context,
        string label)
    {
        var oldQualifiers = GetQualifiers(oldRecord);
        var newQualifiers = GetQualifiers(newRecord);
        var oldMatched = new bool[oldQualifiers.Count];
        var newMatched = new bool[newQualifiers.Count];
        var newByValue = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < newQualifiers.Count; index++)
        {
            var value = ItemPatchNotesMarkdown.CanonicalJson(newQualifiers[index]);
            if (!newByValue.TryGetValue(value, out var indexes)) newByValue.Add(value, indexes = new Queue<int>());
            indexes.Enqueue(index);
        }

        for (var index = 0; index < oldQualifiers.Count; index++)
        {
            var value = ItemPatchNotesMarkdown.CanonicalJson(oldQualifiers[index]);
            if (!newByValue.TryGetValue(value, out var indexes) || indexes.Count == 0) continue;
            oldMatched[index] = true;
            newMatched[indexes.Dequeue()] = true;
        }

        var oldByKey = GroupQualifiersByKey(oldQualifiers, oldMatched);
        var newByKey = GroupQualifiersByKey(newQualifiers, newMatched);
        var labelPrefix = label.StartsWith("ingredient ", StringComparison.Ordinal)
            ? label["ingredient ".Length..]
            : label == "result requirement"
                ? "Result"
                : label;
        foreach (var pair in oldByKey)
        {
            if (pair.Value.Count != 1 || !newByKey.TryGetValue(pair.Key, out var newIndexes) ||
                newIndexes.Count != 1) continue;

            var oldIndex = pair.Value[0];
            var newIndex = newIndexes[0];
            oldMatched[oldIndex] = true;
            newMatched[newIndex] = true;
            var oldText = RenderQualifier(oldQualifiers[oldIndex], oldTranslations, context);
            var newText = RenderQualifier(newQualifiers[newIndex], newTranslations, context);
            if (oldText == newText)
                markdown.AppendLine("- Recipe condition changed; see detailed comparison.");
            else
                markdown.Append("- ").Append(labelPrefix).Append(" requirement changed: ").Append(newText)
                    .Append(" (was ").Append(oldText).AppendLine(").");
        }

        foreach (var index in Enumerable.Range(0, newQualifiers.Count).Where(index => !newMatched[index]))
            markdown.Append("- ").Append(labelPrefix).Append(" now requires: ")
                .AppendLine(RenderQualifier(newQualifiers[index], newTranslations, context));
        foreach (var index in Enumerable.Range(0, oldQualifiers.Count).Where(index => !oldMatched[index]))
            markdown.Append("- ").Append(labelPrefix).Append(" no longer requires: ")
                .AppendLine(RenderQualifier(oldQualifiers[index], oldTranslations, context));
    }

    private static Dictionary<string, List<int>> GroupQualifiersByKey(IReadOnlyList<JsonElement> qualifiers,
        bool[] matched)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < qualifiers.Count; index++)
        {
            if (matched[index] || GetKey(qualifiers[index]) is not { Length: > 0 } key) continue;
            if (!groups.TryGetValue(key, out var indexes)) groups.Add(key, indexes = []);
            indexes.Add(index);
        }

        return groups;
    }

    private static string RenderQualifier(
        JsonElement qualifier,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (TryRenderConditionQualifier(qualifier, out var conditionQualifier)) return conditionQualifier;
        return TryRenderSocketRequirement(qualifier, out var requirement)
            ? requirement
            : RenderValue(qualifier, translations, context);
    }

    private static bool TryRenderConditionQualifier(JsonElement qualifier, out string rendered)
    {
        rendered = GetKey(qualifier) switch
        {
            SyntheticStringRegistry.Keys.CubeQualifierForcePrefixN => "Forced prefix",
            SyntheticStringRegistry.Keys.CubeQualifierForceSuffixN => "Forced suffix",
            _ => ""
        };
        return rendered.Length > 0;
    }

    private static string RenderQualifiersInline(
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        var qualifiers = GetQualifiers(record);
        if (qualifiers.Count == 0) return "";
        return " (" + string.Join("; ", qualifiers.Select(value => RenderValue(value, translations, context))) + ")";
    }

    private static void AppendMetadataChange(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        string field,
        string label,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var oldValue = TryGet(oldRecord, field, out var oldFieldValue)
            ? RenderValue(oldFieldValue, oldTranslations, context)
            : "(none)";
        var newValue = TryGet(newRecord, field, out var newFieldValue)
            ? RenderValue(newFieldValue, newTranslations, context)
            : "(none)";
        if (oldValue == newValue)
        {
            if (!SameField(oldRecord, newRecord, field))
                markdown.Append("- ").Append(label).Append(" (still shown as ").Append(newValue)
                    .AppendLine("); see detailed comparison.");
            return;
        }

        markdown.Append("- ").Append(label).Append(": ").Append(newValue).Append(" (was ").Append(oldValue)
            .AppendLine(").");
    }

    private static void AppendNotesChange(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        if (SameField(oldRecord, newRecord, "Notes")) return;
        var oldNotes = TryGet(oldRecord, "Notes", out var oldValue)
            ? RenderNotesValue(oldValue, oldTranslations, context)
            : "(none)";
        var newNotes = TryGet(newRecord, "Notes", out var newValue)
            ? RenderNotesValue(newValue, newTranslations, context)
            : "(none)";
        if (oldNotes == newNotes)
        {
            markdown.AppendLine("- Recipe notes changed; see detailed comparison.");
            return;
        }

        markdown.Append("- Crafting category or notes: ").Append(newNotes).Append(" (was ").Append(oldNotes)
            .AppendLine(").");
    }

    private static string RenderNotesValue(JsonElement value, RecipeTranslations translations, RenderContext context) =>
        value.ValueKind == JsonValueKind.Array
            ? string.Join(" / ", value.EnumerateArray().Select(note => RenderValue(note, translations, context)))
            : RenderValue(value, translations, context);

    private static void AppendEnabledChange(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RenderContext context)
    {
        if (SameField(oldRecord, newRecord, "Enabled")) return;
        if (TryGet(newRecord, "Enabled", out var enabled) &&
            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            markdown.AppendLine(enabled.GetBoolean() ? "- Recipe was enabled." : "- Recipe was disabled.");
        else
            AddReview(markdown, context, "Recipe enabled status changed to an unsupported value.");
    }

    private static void AppendOperationReview(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord)
    {
        var changed = false;
        foreach (var field in new[] { "Op", "Param", "Value" })
        {
            if (SameField(oldRecord, newRecord, field)) continue;
            changed = true;
        }

        if (changed) markdown.AppendLine("- Recipe condition changed; see detailed comparison.");
    }


    private static void AppendUnknownRootFields(
        StringBuilder markdown,
        JsonElement newRecord,
        RenderContext context,
        JsonElement? oldRecord)
    {
        if (newRecord.ValueKind != JsonValueKind.Object) return;
        var hasUnknownChange = false;
        foreach (var property in newRecord.EnumerateObject())
        {
            if (property.Name is "Id" or "Index" or "Description" or "RequiredClass" or "Class" or "NumInputs" or
                "ResolvedInputsCount" or "Inputs" or "Outputs" or "Notes" or "Enabled" or "Op" or "Param" or "Value")
                continue;
            if (oldRecord is { } previous && TryGet(previous, property.Name, out var oldValue) &&
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) == ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                continue;
            hasUnknownChange = true;
        }

        if (oldRecord is { ValueKind: JsonValueKind.Object } oldObject)
        {
            foreach (var property in oldObject.EnumerateObject())
            {
                if (property.Name is "Id" or "Index" or "Description" or "RequiredClass" or "Class" or "NumInputs" or
                        "ResolvedInputsCount" or "Inputs" or "Outputs" or "Notes" or "Enabled" or "Op" or "Param"
                        or "Value" ||
                    TryGet(newRecord, property.Name, out _)) continue;
                hasUnknownChange = true;
            }
        }

        if (hasUnknownChange)
            AddReview(markdown, context,
                "Other recipe details changed in a way that cannot be described safely; see detailed comparison.");
    }

    private static void AppendUnknownInputFields(
        StringBuilder markdown,
        JsonElement input,
        RenderContext context)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        foreach (var property in input.EnumerateObject())
        {
            if (property.Name is "Name" or "Quantity" or "Qualifiers" or "RawToken") continue;
            AddReview(markdown, context,
                $"Unrecognized ingredient field `{property.Name}` is present; its meaning is unverified.", 2);
        }
    }

    private static void AppendUnknownInputChanges(
        StringBuilder markdown,
        JsonElement oldInput,
        JsonElement newInput,
        RenderContext context)
    {
        foreach (var property in newInput.EnumerateObject())
        {
            if (property.Name is "Name" or "Quantity" or "Qualifiers" or "RawToken") continue;
            if (TryGet(oldInput, property.Name, out var oldValue) &&
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) == ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                continue;
            AddReview(markdown, context,
                $"Unrecognized ingredient field `{property.Name}` changed; its meaning is unverified.");
        }

        foreach (var property in oldInput.EnumerateObject())
        {
            if (property.Name is "Name" or "Quantity" or "Qualifiers" or "RawToken" ||
                TryGet(newInput, property.Name, out _)) continue;
            AddReview(markdown, context,
                $"Unrecognized ingredient field `{property.Name}` was removed; its meaning is unverified.");
        }
    }

    private static void AppendUnknownOutputFields(
        StringBuilder markdown,
        JsonElement output,
        RenderContext context,
        int depth,
        JsonElement? oldOutput = null)
    {
        if (output.ValueKind != JsonValueKind.Object) return;
        var hasUnknownChange = false;
        foreach (var property in output.EnumerateObject())
        {
            if (property.Name is "Name" or "Quantity" or "Qualifiers" or "OutputChance" or "Lines") continue;
            if (oldOutput is { } previous && TryGet(previous, property.Name, out var oldValue) &&
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) == ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                continue;
            hasUnknownChange = true;
        }

        if (oldOutput is { ValueKind: JsonValueKind.Object } oldObject)
        {
            foreach (var property in oldObject.EnumerateObject())
            {
                if (property.Name is "Name" or "Quantity" or "Qualifiers" or "OutputChance" or "Lines" ||
                    TryGet(output, property.Name, out _)) continue;
                hasUnknownChange = true;
            }
        }

        if (hasUnknownChange)
            AddReview(markdown, context,
                "Other result details changed in a way that cannot be described safely; see detailed comparison.",
                depth);
    }

    private static void AppendValueChange(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        string field,
        string label,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        if (SameField(oldRecord, newRecord, field)) return;
        var oldValue = TryGet(oldRecord, field, out var oldFieldValue)
            ? RenderValue(oldFieldValue, oldTranslations, context)
            : "(not recorded)";
        var newValue = TryGet(newRecord, field, out var newFieldValue)
            ? RenderValue(newFieldValue, newTranslations, context)
            : "(not recorded)";
        if (oldValue == newValue)
        {
            markdown.Append("- ").Append(label).Append(" changed (still shown as ").Append(newValue)
                .AppendLine("); see detailed comparison.");
            return;
        }

        markdown.Append("- ").Append(label).Append(": ").Append(newValue).Append(" (was ").Append(oldValue)
            .AppendLine(").");
    }

    private static string RenderValue(JsonElement value, RecipeTranslations translations, RenderContext context)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            (TryGet(value, "key", out _) || TryGet(value, "nameKey", out _)))
        {
            var cacheKey = ItemPatchNotesMarkdown.CanonicalJson(value);
            if (context.RenderedLines.TryGetValue((translations, cacheKey), out var cached)) return cached;
            var rendered = ItemPatchNotesMarkdown.RenderPropertyLineText(value, translations, context.PropertyState);
            if (rendered == "Property description unavailable; see detailed comparison.")
                rendered = "Translation unavailable; see detailed comparison.";

            context.RenderedLines.Add((translations, cacheKey), rendered);
            return rendered;
        }

        if (value.ValueKind == JsonValueKind.Array)
            return string.Join(", ", value.EnumerateArray().Select(item => RenderValue(item, translations, context)));
        if (value.ValueKind == JsonValueKind.Object)
        {
            return "Translation unavailable; see detailed comparison.";
        }

        return ItemPatchNotesMarkdown.LightEscape(value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : RawJson(value));
    }

    private static void AddReview(StringBuilder markdown, RenderContext context, string message, int depth = 0)
    {
        _ = context;
        markdown.Append(new string(' ', depth * 2)).Append("- Note: ").AppendLine(message);
    }

    private static void AppendTrace(StringBuilder markdown, string key) =>
        markdown.Append("<!-- changes.json: ").Append(CubeRecipeCategory).Append(" / ")
            .Append(key.Replace("--", "-", StringComparison.Ordinal)).AppendLine(" -->");

    private static List<JsonElement> GetArray(JsonElement record, string field) =>
        TryGet(record, field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToList()
            : [];

    private static List<JsonElement> GetQualifiers(JsonElement record)
    {
        var qualifiers = GetArray(record, "Qualifiers");
        return TryGet(record, "Quantity", out _)
            ? qualifiers.Where(qualifier => !IsQuantityQualifier(qualifier)).ToList()
            : qualifiers;
    }

    private static bool IsQuantityQualifier(JsonElement qualifier) =>
        TryGet(qualifier, "key", out var key) && key.ValueKind == JsonValueKind.String &&
        key.GetString() is SyntheticStringRegistry.Keys.CubeQualifierQuantity or
            SyntheticStringRegistry.Keys.CubeQualifierQuantityN;

    private static bool SameField(JsonElement left, JsonElement right, string field)
    {
        var hasLeft = TryGet(left, field, out var leftValue);
        var hasRight = TryGet(right, field, out var rightValue);
        return hasLeft == hasRight && (!hasLeft ||
                                       ItemPatchNotesMarkdown.CanonicalJson(leftValue) ==
                                       ItemPatchNotesMarkdown.CanonicalJson(rightValue));
    }

    private static bool IsEmptyValue(JsonElement value) =>
        value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()) ||
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;

    private static bool TryGet(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value)) return true;
        value = default;
        return false;
    }

    private static bool TryGetInt(JsonElement value, out int number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number)) return true;
        number = 0;
        return false;
    }

    private static bool HasTechnicalConditions(JsonElement record) =>
        new[] { "Op", "Param", "Value" }.Any(field => TryGet(record, field, out _));

    private static bool HasTechnicalConditionChanges(JsonElement oldRecord, JsonElement newRecord) =>
        new[] { "Op", "Param", "Value" }.Any(field => !SameField(oldRecord, newRecord, field));

    private static string RawJson(JsonElement value) => value.GetRawText();

    private sealed class RenderContext
    {
        public ItemPatchNotesMarkdown.RenderState PropertyState { get; } = new() { ConciseRecipeText = true };

        public Dictionary<(RecipeTranslations Translations, string CanonicalLine), string> RenderedLines { get; } = [];
    }

    private sealed record RecipeEntry(
        RecipeChangeKind Kind,
        string Key,
        int? RowIndex,
        JsonElement Record,
        JsonElement OldRecord,
        JsonElement NewRecord,
        string Category,
        string Title);

    private enum RecipeChangeKind
    {
        Modified,
        Added,
        Removed
    }
}
