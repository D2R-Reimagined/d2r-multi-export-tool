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
        AppendSummary(markdown, report, category, entries, oldTranslations, newTranslations, context);
        markdown.AppendLine("## Recipe identity and unmatched definitions");
        markdown.AppendLine();
        markdown.AppendLine(
            "Recipe IDs are derived from recipe ingredients and conditions. An ingredient or condition adjustment can therefore appear as an unmatched removal and addition. These entries are not automatically paired or described as confirmed additions or deletions; compare their source IDs and review whether the recipes were restructured.");
        markdown.AppendLine();

        if (category?.Status != "Compared")
        {
            markdown.Append("## Recipe changes unavailable").AppendLine();
            markdown.AppendLine();
            markdown.Append("The cube-recipe category was not successfully compared (")
                .Append(ComparisonMarkdown.EscapeMarkdown(category?.Status ?? "missing from report"))
                .AppendLine("). No recipe-change counts are inferred.");
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

        AppendIssues(markdown, report, category, warnings: false);
        AppendIssues(markdown, report, category, warnings: true);
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
        if (!TryGet(record, "Notes", out var notes) || notes.ValueKind != JsonValueKind.Array)
            return OtherRecipesCategory;

        var rendered = new List<string>();
        var translated = true;
        foreach (var note in notes.EnumerateArray())
        {
            var before = context.PropertyState.UntranslatedValues;
            var value = RenderValue(note, translations, context);
            if (context.PropertyState.UntranslatedValues != before ||
                value.Contains("**Review:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(value))
                translated = false;
            rendered.Add(value);
        }

        var description = string.Join(" / ", rendered);
        return translated && description.Length > 0 ? description : OtherRecipesCategory;
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
                    ? RenderOutputName(output, translations, context)
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
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        markdown.AppendLine("## Summary");
        markdown.AppendLine();
        ComparisonMarkdown.AppendSnapshotSummary(markdown, "Old", report.OldSnapshot);
        ComparisonMarkdown.AppendSnapshotSummary(markdown, "New", report.NewSnapshot);
        markdown.Append("- English translations (old snapshot): ").AppendLine(oldTranslations.Status + ".");
        markdown.Append("- English translations (new snapshot): ").AppendLine(newTranslations.Status + ".");
        markdown.Append("- Recipe comparison: ")
            .AppendLine(ComparisonMarkdown.InlineCode(category?.Status ?? "missing from report") + ".");

        if (category?.Status == "Compared")
        {
            markdown.AppendLine();
            markdown.AppendLine(
                "| Crafting category | Confirmed matched modifications | Added definitions | Removed definitions |");
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
            markdown.AppendLine();
            markdown.Append("- Total confirmed matched modifications: ").Append(category.Counts.Modified)
                .Append("; unmatched additions: ").Append(category.Counts.Added)
                .Append("; unmatched removals: ").Append(category.Counts.Removed)
                .Append("; unchanged matched recipes not listed: ").Append(category.Counts.Unchanged)
                .AppendLine(".");
            if (category.Counts.Reindexed > 0)
                markdown.Append("- Reindexed matched recipes: ").Append(category.Counts.Reindexed).AppendLine(".");
        }
        else
        {
            markdown.AppendLine(
                "- Recipe change counts: unavailable because the cube-recipe category was not compared.");
        }

        var (errors, warnings) = CountIssues(report, category);
        markdown.Append("- Comparison diagnostics: ").Append(errors).Append(" error(s), ")
            .Append(warnings).AppendLine(" warning(s).");
        markdown.Append("- Untranslated or unsupported values requiring review: ")
            .Append(context.PropertyState.UntranslatedValues).AppendLine(".");
        markdown.Append("- Review items: ").Append(context.ReviewItems).AppendLine(".");
        markdown.AppendLine();
    }

    private static void AppendRecipeSections(
        StringBuilder markdown,
        IReadOnlyCollection<RecipeEntry> entries,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        foreach (var categoryGroup in entries.GroupBy(static entry => entry.Category, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key == OtherRecipesCategory ? 1 : 0)
                     .ThenBy(static group => group.Key, StringComparer.Ordinal))
        {
            markdown.Append("## ").AppendLine(ComparisonMarkdown.EscapeMarkdown(categoryGroup.Key));
            markdown.AppendLine();
            AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Modified, "Confirmed matched modifications",
                oldTranslations, newTranslations, context);
            AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Added, "Added recipe definitions",
                oldTranslations, newTranslations, context);
            AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Removed, "Removed recipe definitions",
                oldTranslations, newTranslations, context);
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

        markdown.Append("### ").AppendLine(heading);
        markdown.AppendLine();
        foreach (var entry in selected)
        {
            AppendTrace(markdown, entry.Key);
            markdown.Append("#### ").AppendLine(entry.Title);
            markdown.AppendLine();
            markdown.Append("- Source: ").Append(ComparisonMarkdown.InlineCode(entry.Key)).Append(", row ")
                .Append(entry.RowIndex?.ToString(CultureInfo.InvariantCulture) ?? "?").AppendLine(".");
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
        AppendDefinitionField(markdown, record, "Description", "Description", translations, context);
        AppendNotes(markdown, record, translations, context);
        AppendInputs(markdown, record, translations, context);
        AppendOutputs(markdown, record, translations, context);

        if (TryGet(record, "Enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            markdown.AppendLine("- This recipe is disabled.");

        AppendInputCountReview(markdown, record, context);
        AppendOperationDefinitionReview(markdown, record, translations, context);
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
        AppendMetadataChange(markdown, oldRecord, newRecord, "Description", "Recipe description", oldTranslations,
            newTranslations, context);
        AppendMetadataChange(markdown, oldRecord, newRecord, "Class", "Class restriction", oldTranslations,
            newTranslations, context);
        AppendMetadataChange(markdown, oldRecord, newRecord, "RequiredClass", "Required class restriction",
            oldTranslations, newTranslations, context);
        AppendNotesChange(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendEnabledChange(markdown, oldRecord, newRecord, context);
        AppendInputChanges(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendOutputChanges(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendInputCountReview(markdown, newRecord, context);
        AppendOperationReview(markdown, oldRecord, oldTranslations, context, newRecord, newTranslations);
        AppendUnknownRootFields(markdown, newRecord, context, oldRecord);

        if (markdown.Length == changesBefore)
            AddReview(markdown, context,
                "The comparison reports a modification but no player-readable difference could be isolated; review this recipe's source record.");
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

    private static void AppendNotes(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (!TryGet(record, "Notes", out var notes) || notes.ValueKind != JsonValueKind.Array ||
            !notes.EnumerateArray().Any()) return;
        markdown.Append("- Crafting category or notes: ")
            .AppendLine(string.Join(" / ", notes.EnumerateArray()
                .Select(note => RenderValue(note, translations, context))));
    }

    private static void AppendInputs(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        markdown.AppendLine("- Required ingredients:");
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
            AppendQualifiers(markdown, input, "Ingredient requirement", translations, context, 2);
            AppendUnknownInputFields(markdown, input, context);
        }
    }

    private static string RenderIngredient(JsonElement input, RecipeTranslations translations, RenderContext context)
    {
        var quantity = TryGet(input, "Quantity", out var value) ? RenderValue(value, translations, context) : "?";
        var name = TryGet(input, "Name", out var itemName)
            ? RenderValue(itemName, translations, context)
            : "<missing ingredient name>";
        return quantity + " × " + name;
    }

    private static void AppendOutputs(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        markdown.AppendLine("- Results:");
        if (!TryGet(record, "Outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object)
        {
            AddReview(markdown, context, "Result data is missing or has an unsupported shape.", 1);
            return;
        }

        var found = false;
        foreach (var slot in OutputSlots)
        {
            if (!TryGet(outputs, slot, out var output)) continue;
            found = true;
            AppendOutputDefinition(markdown, slot, output, translations, context, 1);
        }

        if (!found) markdown.AppendLine("  - No result slots were recorded.");
        foreach (var property in outputs.EnumerateObject().Where(property => !OutputSlots.Contains(property.Name)))
            AddReview(markdown, context,
                $"Unrecognized result field `{property.Name}` is present; its meaning is unverified.", 1);
    }

    private static void AppendOutputDefinition(
        StringBuilder markdown,
        string slot,
        JsonElement output,
        RecipeTranslations translations,
        RenderContext context,
        int depth)
    {
        var indent = new string(' ', depth * 2);
        if (output.ValueKind != JsonValueKind.Object)
        {
            AddReview(markdown, context, $"Result {slot} has an unsupported shape.", depth);
            return;
        }

        markdown.Append(indent).Append("- ").Append(slot).Append(": ")
            .AppendLine(RenderOutputName(output, translations, context));
        AppendQualifiers(markdown, output, "Result requirement", translations, context, depth + 1);
        if (TryGet(output, "OutputChance", out var chance))
            markdown.Append(indent).Append("  - Output chance: ")
                .AppendLine(RenderValue(chance, translations, context));
        if (TryGet(output, "Lines", out var lines))
            AppendPropertyLines(markdown, lines, translations, context, depth + 1);
        AppendUnknownOutputFields(markdown, output, context, depth + 1);
    }

    private static string RenderOutputName(JsonElement output, RecipeTranslations translations, RenderContext context)
    {
        var quantity = TryGet(output, "Quantity", out var quantityValue)
            ? RenderValue(quantityValue, translations, context)
            : "?";
        var name = TryGet(output, "Name", out var nameValue)
            ? RenderValue(nameValue, translations, context)
            : "<missing result name>";
        return quantity + " × " + name;
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

        var before = context.PropertyState.UntranslatedValues;
        foreach (var line in lines.EnumerateArray())
            ItemPatchNotesMarkdown.AppendPropertyLine(markdown, line, translations, context.PropertyState, depth);
        var untranslated = context.PropertyState.UntranslatedValues - before;
        if (untranslated > 0)
        {
            context.ReviewItems += untranslated;
            markdown.Append(new string(' ', depth * 2))
                .AppendLine("- **Review:** one or more output properties retain untranslated keys or arguments.");
        }
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
                .AppendLine(RenderIngredient(newInputs[index], newTranslations, context) + RenderQualifiersInline(
                    newInputs[index], newTranslations, context));
        foreach (var index in Enumerable.Range(0, oldInputs.Count).Where(index => !oldMatched[index]))
            markdown.Append("- Removed ingredient: ")
                .AppendLine(RenderIngredient(oldInputs[index], oldTranslations, context) + RenderQualifiersInline(
                    oldInputs[index], oldTranslations, context));

        var oldOrder = oldInputs.Select(InputSignature).ToArray();
        var newOrder = newInputs.Select(InputSignature).ToArray();
        if (oldOrder.Length == newOrder.Length &&
            oldOrder.Order(StringComparer.Ordinal)
                .SequenceEqual(newOrder.Order(StringComparer.Ordinal), StringComparer.Ordinal) &&
            !oldOrder.SequenceEqual(newOrder, StringComparer.Ordinal))
            AddReview(markdown, context,
                "Ingredient order changed; verify whether the new order affects the recipe's operation.");
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
        var oldName = TryGet(oldInput, "Name", out var oldNameValue)
            ? RenderValue(oldNameValue, oldTranslations, context)
            : "<missing ingredient name>";
        var newName = TryGet(newInput, "Name", out var newNameValue)
            ? RenderValue(newNameValue, newTranslations, context)
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
        if (oldQuantity != newQuantity)
            markdown.Append("- Required ingredient quantity: ").Append(newQuantity).Append(" × ").Append(newName)
                .Append(" (was ").Append(oldQuantity).Append(" × ").Append(oldName).AppendLine(").");
        AppendQualifierChanges(markdown, oldInput, newInput, oldTranslations, newTranslations, context,
            "ingredient requirement");
        AppendUnknownInputChanges(markdown, oldInput, newInput, context);
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

        foreach (var slot in OutputSlots)
        {
            var hasOld = TryGet(oldOutputs, slot, out var oldOutput);
            var hasNew = TryGet(newOutputs, slot, out var newOutput);
            if (!hasOld && hasNew)
            {
                markdown.Append("- Added result slot ").Append(slot).AppendLine(":");
                AppendOutputDefinition(markdown, slot, newOutput, newTranslations, context, 1);
                continue;
            }

            if (hasOld && !hasNew)
            {
                markdown.Append("- Removed result slot ").Append(slot).AppendLine(":");
                AppendOutputDefinition(markdown, slot, oldOutput, oldTranslations, context, 1);
                continue;
            }

            if (hasOld && hasNew)
                AppendOutputChange(markdown, slot, oldOutput, newOutput, oldTranslations, newTranslations, context);
        }

        foreach (var property in oldOutputs.EnumerateObject().Where(property => !OutputSlots.Contains(property.Name)))
            AddReview(markdown, context, $"Result field `{property.Name}` was present before; verify its removal.");
        foreach (var property in newOutputs.EnumerateObject().Where(property => !OutputSlots.Contains(property.Name)))
        {
            if (!TryGet(oldOutputs, property.Name, out var oldValue) ||
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) != ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                AddReview(markdown, context,
                    $"Unrecognized result field `{property.Name}` changed; its meaning is unverified.");
        }
    }

    private static void AppendOutputChange(
        StringBuilder markdown,
        string slot,
        JsonElement oldOutput,
        JsonElement newOutput,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        if (oldOutput.ValueKind != JsonValueKind.Object || newOutput.ValueKind != JsonValueKind.Object)
        {
            if (ItemPatchNotesMarkdown.CanonicalJson(oldOutput) != ItemPatchNotesMarkdown.CanonicalJson(newOutput))
                AddReview(markdown, context, $"Result slot {slot} changed shape; review its meaning.");
            return;
        }

        var oldName = TryGet(oldOutput, "Name", out var oldNameValue)
            ? RenderValue(oldNameValue, oldTranslations, context)
            : "<missing result name>";
        var newName = TryGet(newOutput, "Name", out var newNameValue)
            ? RenderValue(newNameValue, newTranslations, context)
            : "<missing result name>";
        if (oldName != newName)
            markdown.Append("- Output item (").Append(slot).Append("): ").Append(newName).Append(" (was ")
                .Append(oldName).AppendLine(").");

        var oldQuantity = TryGet(oldOutput, "Quantity", out var oldQuantityValue)
            ? RenderValue(oldQuantityValue, oldTranslations, context)
            : "?";
        var newQuantity = TryGet(newOutput, "Quantity", out var newQuantityValue)
            ? RenderValue(newQuantityValue, newTranslations, context)
            : "?";
        if (oldQuantity != newQuantity)
            markdown.Append("- Output quantity (").Append(slot).Append("): ").Append(newQuantity).Append(" × ")
                .Append(newName).Append(" (was ").Append(oldQuantity).Append(" × ").Append(oldName).AppendLine(").");

        AppendQualifierChanges(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context,
            $"result requirement ({slot})");
        AppendValueChange(markdown, oldOutput, newOutput, "OutputChance", $"Output chance ({slot})",
            oldTranslations, newTranslations, context);
        AppendOutputPropertyChanges(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context, slot);
        AppendUnknownOutputFields(markdown, newOutput, context, 0, oldOutput);
    }

    private static void AppendOutputPropertyChanges(
        StringBuilder markdown,
        JsonElement oldOutput,
        JsonElement newOutput,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context,
        string slot)
    {
        var oldLines = GetArray(oldOutput, "Lines");
        var newLines = GetArray(newOutput, "Lines");
        var before = context.PropertyState.UntranslatedValues;
        var diff = ItemPatchNotesMarkdown.DiffProperties(oldLines, newLines, oldTranslations, newTranslations,
            context.PropertyState);
        var untranslated = context.PropertyState.UntranslatedValues - before;
        foreach (var change in diff.Changes)
        {
            if (change == "Property order changed.")
            {
                AddReview(markdown, context,
                    $"Output {slot} property order changed; review whether it affects behavior.");
                continue;
            }

            if (change.StartsWith("Changed: ", StringComparison.Ordinal))
                markdown.Append("- Output property (").Append(slot).AppendLine("): " + change[9..]);
            else if (change.StartsWith("Added: ", StringComparison.Ordinal))
                markdown.Append("- Added output property (").Append(slot).AppendLine("): " + change[7..]);
            else if (change.StartsWith("Removed: ", StringComparison.Ordinal))
                markdown.Append("- Removed output property (").Append(slot).AppendLine("): " + change[9..]);
            else
                AddReview(markdown, context, $"Output {slot} property difference needs review: {change}");
        }

        if (untranslated > 0)
        {
            context.ReviewItems += untranslated;
            markdown.Append("- **Review:** output ").Append(slot)
                .AppendLine(
                    " property translation is unavailable or unsupported; original keys and arguments are retained.");
        }
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

        foreach (var index in Enumerable.Range(0, newQualifiers.Count).Where(index => !newMatched[index]))
            markdown.Append("- Added ").Append(label).Append(": ")
                .AppendLine(RenderValue(newQualifiers[index], newTranslations, context));
        foreach (var index in Enumerable.Range(0, oldQualifiers.Count).Where(index => !oldMatched[index]))
            markdown.Append("- Removed ").Append(label).Append(": ")
                .AppendLine(RenderValue(oldQualifiers[index], oldTranslations, context));
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
        if (oldValue == newValue) return;
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
        if (oldNotes == newNotes) return;
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
        RecipeTranslations oldTranslations,
        RenderContext context,
        JsonElement newRecord,
        RecipeTranslations newTranslations)
    {
        foreach (var field in new[] { "Op", "Param", "Value" })
        {
            if (SameField(oldRecord, newRecord, field)) continue;
            if (!TryGet(oldRecord, field, out var oldValue))
            {
                if (!TryGet(newRecord, field, out var addedValue)) continue;
                var added = RenderValue(addedValue, newTranslations, context);
                AddReview(markdown, context,
                    $"Recipe operation field {field} is {added}; its gameplay meaning has not been verified.");
                continue;
            }

            var before = RenderValue(oldValue, oldTranslations, context);
            var after = TryGet(newRecord, field, out var newValue)
                ? RenderValue(newValue, newTranslations, context)
                : "(not recorded)";
            AddReview(markdown, context,
                $"Recipe {field} changed ({before} → {after}); its gameplay meaning has not been verified.");
        }
    }

    private static void AppendOperationDefinitionReview(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderContext context)
    {
        var fields = new List<string>();
        foreach (var field in new[] { "Op", "Param", "Value" })
        {
            if (!TryGet(record, field, out var value)) continue;
            fields.Add(field + "=" + RenderValue(value, translations, context));
        }

        if (fields.Count > 0)
            AddReview(markdown, context,
                $"Recipe operation fields ({string.Join(", ", fields)}) are present; their gameplay meaning has not been verified.");
    }

    private static void AppendInputCountReview(StringBuilder markdown, JsonElement record, RenderContext context)
    {
        var inputs = GetArray(record, "Inputs");
        var totalQuantity = 0;
        var quantitiesKnown = true;
        foreach (var input in inputs)
        {
            if (TryGet(input, "Quantity", out var quantity) && TryGetInt(quantity, out var value))
                totalQuantity += value;
            else
                quantitiesKnown = false;
        }

        var declared = 0;
        var resolved = 0;
        var hasDeclared = TryGet(record, "NumInputs", out var declaredValue) &&
                          TryGetInt(declaredValue, out declared);
        var hasResolved = TryGet(record, "ResolvedInputsCount", out var resolvedValue) &&
                          TryGetInt(resolvedValue, out resolved);
        if (!hasDeclared && !hasResolved) return;
        var declaredMismatch = hasDeclared && (!quantitiesKnown || declared != totalQuantity);
        var resolvedMismatch = hasResolved && resolved != inputs.Count;
        if (!declaredMismatch && !resolvedMismatch) return;
        var declaredText = hasDeclared ? declared.ToString(CultureInfo.InvariantCulture) : "not recorded";
        var resolvedText = hasResolved ? resolved.ToString(CultureInfo.InvariantCulture) : "not recorded";
        AddReview(markdown, context,
            $"Input count needs review: {inputs.Count} ingredient records and " +
            $"{(quantitiesKnown ? totalQuantity.ToString(CultureInfo.InvariantCulture) : "unknown")} total quantity, " +
            $"{declaredText} declared total and {resolvedText} resolved records.");
    }

    private static void AppendUnknownRootFields(
        StringBuilder markdown,
        JsonElement newRecord,
        RenderContext context,
        JsonElement? oldRecord)
    {
        if (newRecord.ValueKind != JsonValueKind.Object) return;
        foreach (var property in newRecord.EnumerateObject())
        {
            if (property.Name is "Id" or "Index" or "Description" or "RequiredClass" or "Class" or "NumInputs" or
                "ResolvedInputsCount" or "Inputs" or "Outputs" or "Notes" or "Enabled" or "Op" or "Param" or "Value")
                continue;
            if (oldRecord is { } previous && TryGet(previous, property.Name, out var oldValue) &&
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) == ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                continue;
            AddReview(markdown, context,
                $"Unrecognized recipe field `{property.Name}` changed or is present; its gameplay meaning is unverified.");
        }

        if (oldRecord is not { ValueKind: JsonValueKind.Object } oldObject) return;
        foreach (var property in oldObject.EnumerateObject())
        {
            if (property.Name is "Id" or "Index" or "Description" or "RequiredClass" or "Class" or "NumInputs" or
                    "ResolvedInputsCount" or "Inputs" or "Outputs" or "Notes" or "Enabled" or "Op" or "Param"
                    or "Value" ||
                TryGet(newRecord, property.Name, out _)) continue;
            AddReview(markdown, context,
                $"Unrecognized recipe field `{property.Name}` was removed; its gameplay meaning is unverified.");
        }
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
        foreach (var property in output.EnumerateObject())
        {
            if (property.Name is "Name" or "Quantity" or "Qualifiers" or "OutputChance" or "Lines") continue;
            if (oldOutput is { } previous && TryGet(previous, property.Name, out var oldValue) &&
                ItemPatchNotesMarkdown.CanonicalJson(oldValue) == ItemPatchNotesMarkdown.CanonicalJson(property.Value))
                continue;
            AddReview(markdown, context,
                $"Unrecognized result field `{property.Name}` changed or is present; its meaning is unverified.",
                depth);
        }

        if (oldOutput is not { ValueKind: JsonValueKind.Object } oldObject) return;
        foreach (var property in oldObject.EnumerateObject())
        {
            if (property.Name is "Name" or "Quantity" or "Qualifiers" or "OutputChance" or "Lines" ||
                TryGet(output, property.Name, out _)) continue;
            AddReview(markdown, context,
                $"Unrecognized result field `{property.Name}` was removed; its meaning is unverified.", depth);
        }
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
            var before = context.PropertyState.UntranslatedValues;
            var rendered = ItemPatchNotesMarkdown.RenderPropertyLineText(value, translations, context.PropertyState);
            if (context.PropertyState.UntranslatedValues > before)
            {
                context.ReviewItems++;
                rendered += " (**Review: translation unavailable or unsupported.**)";
            }

            context.RenderedLines.Add((translations, cacheKey), rendered);
            return rendered;
        }

        if (value.ValueKind == JsonValueKind.Array)
            return string.Join(", ", value.EnumerateArray().Select(item => RenderValue(item, translations, context)));
        if (value.ValueKind == JsonValueKind.Object)
        {
            context.ReviewItems++;
            return "(structured value; review required)";
        }

        return ItemPatchNotesMarkdown.LightEscape(value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : RawJson(value));
    }

    private static void AppendIssues(
        StringBuilder markdown,
        ComparisonReport report,
        CategoryComparison? category,
        bool warnings)
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
        if (category is not null) Add(warnings ? category.Warnings : category.Errors);
        if (issues.Count > 0)
            ComparisonMarkdown.AppendIssues(markdown, issues,
                warnings ? "Comparison warnings" : "Comparison errors");
    }

    private static (int Errors, int Warnings) CountIssues(ComparisonReport report, CategoryComparison? category)
    {
        static int Count(IEnumerable<ComparisonIssue> issues) =>
            issues.Select(static issue => (issue.Snapshot, issue.Code, issue.Message)).Distinct().Count();

        var errors = Count(report.OldSnapshot.Errors.Concat(report.NewSnapshot.Errors)
            .Concat(category?.Errors ?? []));
        var warnings = Count(report.OldSnapshot.Warnings.Concat(report.NewSnapshot.Warnings)
            .Concat(category?.Warnings ?? []));
        return (errors, warnings);
    }

    private static void AddReview(StringBuilder markdown, RenderContext context, string message, int depth = 0)
    {
        context.ReviewItems++;
        markdown.Append(new string(' ', depth * 2)).Append("- **Review:** ").AppendLine(message);
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

    private static string RawJson(JsonElement value) => value.GetRawText();

    private sealed class RenderContext
    {
        public ItemPatchNotesMarkdown.RenderState PropertyState { get; } = new();

        public Dictionary<(RecipeTranslations Translations, string CanonicalLine), string> RenderedLines { get; } = [];

        public int ReviewItems { get; set; }
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
