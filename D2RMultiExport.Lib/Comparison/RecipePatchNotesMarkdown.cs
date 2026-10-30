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

    /// <summary>
    /// Verified from misc.txt: rvs is Rejuvenation Potion although its enUS value is the malformed template "35%".
    /// </summary>
    private static readonly Dictionary<string, (string EnglishValue, string DisplayName)> VerifiedItemNameFallbacks =
        new(StringComparer.Ordinal)
        {
            ["rvs"] = ("35%", "Rejuvenation Potion")
        };

    /// <summary>
    /// Verified cubemain.txt migration of the unique-jewel ingredient from base item code <c>jew</c> (name key
    /// <c>jew</c>) to item-type code <c>jewl</c> (name key <c>jewlitype</c>). It rotates content-derived recipe
    /// ids without changing the recipe, so an otherwise identical one-to-one removed/added pair is omitted from
    /// the player-facing notes only; <c>jew</c> and <c>jewl</c> are not treated as equivalent anywhere else.
    /// </summary>
    private static readonly (string OldCode, string OldNameKey, string NewCode, string NewNameKey, string Quality)
        UniqueJewelMigration = ("jew", "jew", "jewl", "jewlitype", "uni");

    private static readonly string UniqueJewelMigrationNewName = ItemPatchNotesMarkdown.CanonicalJson(
        JsonSerializer.SerializeToElement(new { key = UniqueJewelMigration.NewNameKey, args = Array.Empty<object>() }));

    public static async Task<string> RenderAsync(ComparisonReport report)
    {
        var oldTranslations = await RecipeTranslations.LoadAsync(report.OldSnapshot);
        var newTranslations = await RecipeTranslations.LoadAsync(report.NewSnapshot);
        var context = new RenderContext();
        var category = report.Categories.FirstOrDefault(item => item.Category == CubeRecipeCategory);
        var migrationPairs = category?.Status == "Compared"
            ? FindUniqueJewelMigrationPairs(category)
            : new MigrationPairs([], []);
        var entries = category?.Status == "Compared"
            ? BuildEntries(category, migrationPairs, oldTranslations, newTranslations, context)
            : [];
        var sections = new StringBuilder();
        if (category?.Status == "Compared")
            AppendRecipeSections(sections, entries, oldTranslations, newTranslations, context);

        var markdown = new StringBuilder();
        markdown.AppendLine("# Recipe patch notes");
        markdown.AppendLine();
        if (category?.Status != "Compared")
        {
            markdown.AppendLine("## Recipe changes");
            markdown.AppendLine();
            markdown.AppendLine("*Recipe changes are not available.*");
            markdown.AppendLine();
            return markdown.ToString();
        }

        AppendSummary(markdown, entries);
        if (entries.Count == 0)
        {
            markdown.AppendLine("## Recipe changes");
            markdown.AppendLine();
            markdown.AppendLine("*No recipe changes.*");
            markdown.AppendLine();
        }
        else
            markdown.Append(sections);

        return markdown.ToString();
    }

    private static List<RecipeEntry> BuildEntries(
        CategoryComparison category,
        MigrationPairs migrationPairs,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var entries = new List<RecipeEntry>(category.Counts.Added + category.Counts.Removed +
                                            category.Counts.Modified);
        foreach (var record in category.ModifiedRecords)
            entries.Add(CreateEntry(RecipeChangeKind.Modified, record.Key, record.NewRowIndex, record.New,
                record.Old, record.New, newTranslations, context));
        foreach (var record in category.AddedRecords.Where(record => !migrationPairs.Added.Contains(record.Key)))
            entries.Add(CreateEntry(RecipeChangeKind.Added, record.Key, record.RowIndex, record.Record,
                default, record.Record, newTranslations, context));
        foreach (var record in category.RemovedRecords.Where(record => !migrationPairs.Removed.Contains(record.Key)))
            entries.Add(CreateEntry(RecipeChangeKind.Removed, record.Key, record.RowIndex, record.Record,
                record.Record, default, oldTranslations, context));

        return entries;
    }

    /// <summary>
    /// Pairs removed and added records that are identical except for their identity, index and the
    /// <see cref="UniqueJewelMigration"/> ingredient substitution. Only signatures shared by exactly one removed
    /// and one added record are paired; anything ambiguous or otherwise different stays visible.
    /// </summary>
    private static MigrationPairs FindUniqueJewelMigrationPairs(CategoryComparison category)
    {
        var removedBySignature = GroupByMigrationSignature(category.RemovedRecords, oldSide: true);
        var addedBySignature = GroupByMigrationSignature(category.AddedRecords, oldSide: false);
        var pairs = new MigrationPairs([], []);
        foreach (var (signature, removedKeys) in removedBySignature)
        {
            if (removedKeys.Count != 1 || !addedBySignature.TryGetValue(signature, out var addedKeys) ||
                addedKeys.Count != 1) continue;
            pairs.Removed.Add(removedKeys[0]);
            pairs.Added.Add(addedKeys[0]);
        }

        return pairs;
    }

    private static Dictionary<string, List<string>> GroupByMigrationSignature(
        IEnumerable<RecordEntry> records,
        bool oldSide)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (MigrationSignature(record.Record, oldSide) is not { } signature) continue;
            if (!groups.TryGetValue(signature, out var keys)) groups.Add(signature, keys = []);
            keys.Add(record.Key);
        }

        return groups;
    }

    /// <summary>
    /// Canonical record content without <c>Id</c>/<c>Index</c>, with each migrated old-side unique-jewel
    /// ingredient rewritten to its new code and name key; null when the record has no migrated ingredient.
    /// </summary>
    private static string? MigrationSignature(JsonElement record, bool oldSide)
    {
        if (record.ValueKind != JsonValueKind.Object) return null;
        var migrated = 0;
        var signature = new StringBuilder("{");
        foreach (var property in record.EnumerateObject().OrderBy(static property => property.Name,
                     StringComparer.Ordinal))
        {
            if (property.Name is "Id" or "Index") continue;
            if (property.Name == "Details" && RecordComparer.HasNoRecipeDetails(record)) continue;
            signature.Append(JsonSerializer.Serialize(property.Name)).Append(':');
            if (property.Name != "Inputs" || property.Value.ValueKind != JsonValueKind.Array)
            {
                signature.Append(ItemPatchNotesMarkdown.CanonicalJson(property.Value)).Append(',');
                continue;
            }

            signature.Append('[');
            foreach (var input in property.Value.EnumerateArray())
            {
                if (!IsUniqueJewelMigrationInput(input, oldSide))
                {
                    signature.Append(ItemPatchNotesMarkdown.CanonicalJson(input)).Append(',');
                    continue;
                }

                migrated++;
                signature.Append(oldSide ? MigratedInputSignature(input) : ItemPatchNotesMarkdown.CanonicalJson(input))
                    .Append(',');
            }

            signature.Append("],");
        }

        return migrated > 0 ? signature.Append('}').ToString() : null;
    }

    private static bool IsUniqueJewelMigrationInput(JsonElement input, bool oldSide)
    {
        var code = oldSide ? UniqueJewelMigration.OldCode : UniqueJewelMigration.NewCode;
        var nameKey = oldSide ? UniqueJewelMigration.OldNameKey : UniqueJewelMigration.NewNameKey;
        if (!TryGet(input, "RawToken", out var rawToken) || rawToken.ValueKind != JsonValueKind.String ||
            !TryGet(input, "Name", out var name) || name.ValueKind != JsonValueKind.Object ||
            name.EnumerateObject().Count() != 2 || !TryGet(name, "key", out var key) ||
            key.ValueKind != JsonValueKind.String || key.GetString() != nameKey ||
            !TryGet(name, "args", out var args) || args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() != 0) return false;

        var parts = UnquoteRawToken(rawToken.GetString() ?? "").Split(',');
        return parts[0] == code && parts.Skip(1).Contains(UniqueJewelMigration.Quality, StringComparer.Ordinal);
    }

    private static string MigratedInputSignature(JsonElement input)
    {
        var signature = new StringBuilder("{");
        foreach (var property in input.EnumerateObject().OrderBy(static property => property.Name,
                     StringComparer.Ordinal))
        {
            if (signature.Length > 1) signature.Append(',');
            signature.Append(JsonSerializer.Serialize(property.Name)).Append(':');
            if (property.Name == "Name")
                signature.Append(UniqueJewelMigrationNewName);
            else if (property.Name == "RawToken")
            {
                var rawToken = property.Value.GetString() ?? "";
                var quoted = rawToken.Length >= 2 && rawToken[0] == '"' && rawToken[^1] == '"';
                var token = UnquoteRawToken(rawToken);
                token = UniqueJewelMigration.NewCode + token[UniqueJewelMigration.OldCode.Length..];
                signature.Append(JsonSerializer.Serialize(quoted ? '"' + token + '"' : token));
            }
            else
                signature.Append(ItemPatchNotesMarkdown.CanonicalJson(property.Value));
        }

        return signature.Append('}').ToString();
    }

    private static string UnquoteRawToken(string rawToken) =>
        rawToken.Length >= 2 && rawToken[0] == '"' && rawToken[^1] == '"' ? rawToken[1..^1] : rawToken;

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
        var categoryKey = GetRecipeCategoryKey(record);
        var category = GetRecipeCategory(record, translations, context);
        var title = GetRecipeTitle(record, translations, context);
        if (TryGet(record, "RequiredClass", out var requiredClass) &&
            requiredClass.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(requiredClass.GetString()))
            title = RenderValue(requiredClass, translations, context) + "-only recipe: " + title;
        return new RecipeEntry(kind, key, rowIndex, record, oldRecord, newRecord, categoryKey, category, title);
    }

    private static string GetRecipeCategoryKey(JsonElement record)
    {
        if (!TryGet(record, "Notes", out var notes) || notes.ValueKind != JsonValueKind.Array ||
            notes.GetArrayLength() == 0 || !TryGet(notes[0], "key", out var key) ||
            key.ValueKind != JsonValueKind.String)
            return "";

        return key.GetString() ?? "";
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
        if (TryGet(record, "Description", out var description) &&
            RenderValue(description, translations, context) is { } descriptionText)
            return descriptionText;

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

    private static void AppendSummary(StringBuilder markdown, IReadOnlyCollection<RecipeEntry> entries)
    {
        markdown.AppendLine("## Summary");
        markdown.AppendLine();
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
            if (categoryGroup.Key != OtherRecipesCategory && categoryGroup.All(static entry =>
                    entry.CategoryKey == SyntheticStringRegistry.Keys.CubeNoteItemEnchantment))
                AppendEnchantmentTargetSections(markdown, categoryGroup, oldTranslations, newTranslations, context);
            else
            {
                AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Modified, "Modified recipes",
                    oldTranslations, newTranslations, context);
                AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Added, "Added recipes",
                    oldTranslations, newTranslations, context);
                AppendEntrySection(markdown, categoryGroup, RecipeChangeKind.Removed, "Removed recipes",
                    oldTranslations, newTranslations, context);
            }

            ComparisonMarkdown.AppendDetailsEnd(markdown);
        }
    }

    private static void AppendEnchantmentTargetSections(
        StringBuilder markdown,
        IEnumerable<RecipeEntry> entries,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context)
    {
        var targetGroups = entries.GroupBy(static entry => GetTargetTypeKey(entry.Record), StringComparer.Ordinal)
            .Select(group =>
            {
                var representative = group.OrderBy(static entry => entry.Kind == RecipeChangeKind.Removed ? 1 : 0)
                    .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
                    .First();
                var translations = representative.Kind == RecipeChangeKind.Removed
                    ? oldTranslations
                    : newTranslations;
                var inputs = GetArray(representative.Record, "Inputs");
                var heading = inputs.Count > 0
                    ? RenderItemName(inputs[0], translations, context)
                    : "Unidentified item type";
                return (group.Key, Entries: group.ToArray(), Heading: heading);
            })
            .OrderBy(static group => group.Heading, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .ToArray();

        foreach (var targetGroup in targetGroups)
        {
            ComparisonMarkdown.AppendDetailsStart(markdown,
                $"{targetGroup.Heading} ({ComparisonMarkdown.Invariant(targetGroup.Entries.Length)})");
            AppendEntrySection(markdown, targetGroup.Entries, RecipeChangeKind.Modified, "Modified recipes",
                oldTranslations, newTranslations, context, 4);
            AppendEntrySection(markdown, targetGroup.Entries, RecipeChangeKind.Added, "Added recipes",
                oldTranslations, newTranslations, context, 4);
            AppendEntrySection(markdown, targetGroup.Entries, RecipeChangeKind.Removed, "Removed recipes",
                oldTranslations, newTranslations, context, 4);
            ComparisonMarkdown.AppendDetailsEnd(markdown);
        }
    }

    private static string GetTargetTypeKey(JsonElement record)
    {
        var inputs = GetArray(record, "Inputs");
        return inputs.Count > 0 && TryGet(inputs[0], "Name", out var name) ? GetKey(name) ?? "" : "";
    }

    private static void AppendEntrySection(
        StringBuilder markdown,
        IEnumerable<RecipeEntry> entries,
        RecipeChangeKind kind,
        string heading,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context,
        int headingLevel = 3)
    {
        var selected = entries.Where(entry => entry.Kind == kind)
            .OrderBy(static entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0) return;

        ComparisonMarkdown.AppendDetailsStart(markdown,
            $"{heading} ({ComparisonMarkdown.Invariant(selected.Length)})");
        foreach (var entry in selected)
        {
            markdown.Append(new string('#', headingLevel + 1)).Append(' ').AppendLine(entry.Title).AppendLine();
            AppendAssignedNotes(markdown, entry.Record, entry.Category,
                kind == RecipeChangeKind.Removed ? oldTranslations : newTranslations, context);
            if (kind == RecipeChangeKind.Modified)
                AppendModifiedRecipe(markdown, entry, oldTranslations, newTranslations, context);
            else
                AppendRecipeDefinition(markdown, entry.Record,
                    kind == RecipeChangeKind.Added ? newTranslations : oldTranslations, context);
            markdown.AppendLine();
        }

        ComparisonMarkdown.AppendDetailsEnd(markdown);
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
        AppendMetadataChange(markdown, oldRecord, newRecord, "Description", "Recipe description changed",
            oldTranslations,
            newTranslations, context);
        AppendMetadataChange(markdown, oldRecord, newRecord, "Class", "Class restriction changed", oldTranslations,
            newTranslations, context);
        AppendMetadataChange(markdown, oldRecord, newRecord, "RequiredClass", "Required class restriction changed",
            oldTranslations, newTranslations, context);
        AppendNotesChange(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendEnabledChange(markdown, oldRecord, newRecord);
        AppendInputChanges(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
        AppendOutputChanges(markdown, oldRecord, newRecord, oldTranslations, newTranslations, context);
    }

    private static void AppendDefinitionField(
        StringBuilder markdown,
        JsonElement record,
        string field,
        string label,
        RecipeTranslations translations,
        RenderContext context)
    {
        if (!TryGet(record, field, out var value) || IsEmptyValue(value) ||
            RenderValue(value, translations, context) is not { } rendered) return;
        markdown.Append("- ").Append(label).Append(": ").AppendLine(rendered);
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
        var labels = (category == OtherRecipesCategory ? rendered : rendered.Skip(1)).OfType<string>().ToArray();
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
        if (!TryGet(record, "Inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array) return;
        markdown.AppendLine("- Ingredients:");
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
        if (!TryGet(record, "Outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object) return;

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
        if (output.ValueKind != JsonValueKind.Object) return;

        if (showResultName || multipleOutputs)
            markdown.Append(indent).Append("- ")
                .AppendLine(RenderOutputName(output, translations, context, inputs));
        var nestedIndent = new string(' ', (showResultName || multipleOutputs ? depth + 1 : depth) * 2);
        if (TryGet(output, "OutputChance", out var chance) && RenderValue(chance, translations, context) is { } chanceText)
            markdown.Append(nestedIndent).Append("- Result chance: ").AppendLine(chanceText);
        if (TryGet(output, "Lines", out var lines))
            AppendPropertyLines(markdown, lines, translations, context,
                depth + (showResultName || multipleOutputs ? 1 : 0));
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
        var nameKey = GetKey(name);
        if (nameKey is not null && VerifiedItemNameFallbacks.TryGetValue(nameKey, out var fallback) &&
            translations.TryGet(nameKey, out var englishValue) && englishValue == fallback.EnglishValue)
            return fallback.DisplayName;

        return RenderValue(name, translations, context) is { } rendered
            ? RemoveRedundantRuneIndex(rendered, nameKey)
            : "Unknown item";
    }

    private static string RemoveRedundantRuneIndex(string name, string? nameKey)
    {
        if (nameKey is not { Length: 3 } || nameKey[0] != 'r' ||
            !int.TryParse(nameKey.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var runeIndex) ||
            runeIndex is < 1 or > 33)
            return name;

        var suffix = $" (#{runeIndex.ToString(CultureInfo.InvariantCulture)})";
        return name.EndsWith(" Rune" + suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
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

            if (RenderValue(qualifier, translations, context) is not { } text) continue;
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

    private static void AppendPropertyLines(
        StringBuilder markdown,
        JsonElement lines,
        RecipeTranslations translations,
        RenderContext context,
        int depth)
    {
        if (lines.ValueKind != JsonValueKind.Array) return;
        foreach (var line in lines.EnumerateArray())
        {
            if (ItemPatchNotesMarkdown.RenderPropertyLineText(line, translations, context.PropertyState,
                    translations.VerifiedFlagKeys, translations.VerifiedTrailingValueKeys) is { } rendered)
                markdown.Append(new string(' ', depth * 2)).Append("- ").AppendLine(rendered);
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
                .AppendLine(RenderIngredient(newInputs[index], newTranslations, context));
        foreach (var index in Enumerable.Range(0, oldInputs.Count).Where(index => !oldMatched[index]))
            markdown.Append("- Removed ingredient: ")
                .AppendLine(RenderIngredient(oldInputs[index], oldTranslations, context));
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
        var oldName = RenderItemName(oldInput, oldTranslations, context);
        var newName = RenderItemName(newInput, newTranslations, context);
        if (oldName != newName)
            markdown.Append("- Required ingredient: ").Append(newName).Append(" (was ").Append(oldName)
                .AppendLine(").");
        if (TryRenderQuantityChange(oldInput, newInput, oldTranslations, newTranslations, context,
                out var oldQuantity, out var newQuantity))
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
            return;

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
        if (oldOutput.ValueKind != JsonValueKind.Object || newOutput.ValueKind != JsonValueKind.Object) return;

        var oldName = RenderOutputName(oldOutput, oldTranslations, context, oldInputs, includeQuantity: false);
        var newName = RenderOutputName(newOutput, newTranslations, context, newInputs, includeQuantity: false);
        if (oldName != newName)
            markdown.Append("- Result: ").Append(newName).Append(" (was ").Append(oldName).AppendLine(").");

        if (TryRenderQuantityChange(oldOutput, newOutput, oldTranslations, newTranslations, context,
                out var oldQuantity, out var newQuantity))
            markdown.Append("- Result quantity: ").Append(FormatQuantity(newQuantity, newName)).Append(" (was ")
                .Append(FormatQuantity(oldQuantity, oldName)).AppendLine(").");

        AppendQualifierChanges(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context,
            "result requirement");
        AppendValueChange(markdown, oldOutput, newOutput, "OutputChance", "Result chance",
            oldTranslations, newTranslations, context);
        AppendOutputPropertyChanges(markdown, oldOutput, newOutput, oldTranslations, newTranslations, context,
            newName, multipleResults);
    }

    private static bool TryRenderQuantityChange(
        JsonElement oldItem,
        JsonElement newItem,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderContext context,
        out string oldQuantity,
        out string newQuantity)
    {
        oldQuantity = TryGet(oldItem, "Quantity", out var oldValue)
            ? RenderValue(oldValue, oldTranslations, context) ?? ""
            : "";
        newQuantity = TryGet(newItem, "Quantity", out var newValue)
            ? RenderValue(newValue, newTranslations, context) ?? ""
            : "";
        return oldQuantity.Length > 0 && newQuantity.Length > 0 && oldQuantity != newQuantity;
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
        var resultPrefix = multipleResults ? resultName + " — " : "";
        foreach (var change in diff.Changes)
        {
            if (change.StartsWith("Changed: ", StringComparison.Ordinal))
                markdown.Append("- Changed: ").Append(resultPrefix).AppendLine(change[9..]);
            else if (change.StartsWith("Added: ", StringComparison.Ordinal))
                markdown.Append("- Added: ").Append(resultPrefix).AppendLine(change[7..]);
            else if (change.StartsWith("Removed: ", StringComparison.Ordinal))
                markdown.Append("- Removed: ").Append(resultPrefix).AppendLine(change[9..]);
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
            if (oldText is null || newText is null) continue;
            if (oldText == newText)
            {
                if (labelPrefix == "Result" &&
                    GetKey(oldQualifiers[oldIndex]) == SyntheticStringRegistry.Keys.CubeQualifierForcePrefixN)
                    markdown.AppendLine("- Result forced magic prefix changed.");
                continue;
            }

            markdown.Append("- ").Append(labelPrefix).Append(" requirement changed: ").Append(newText)
                .Append(" (was ").Append(oldText).AppendLine(").");
        }

        foreach (var index in Enumerable.Range(0, newQualifiers.Count).Where(index => !newMatched[index]))
        {
            if (RenderQualifier(newQualifiers[index], newTranslations, context) is { } text)
                markdown.Append("- ").Append(labelPrefix).Append(" now requires: ").AppendLine(text);
        }

        foreach (var index in Enumerable.Range(0, oldQualifiers.Count).Where(index => !oldMatched[index]))
        {
            if (RenderQualifier(oldQualifiers[index], oldTranslations, context) is { } text)
                markdown.Append("- ").Append(labelPrefix).Append(" no longer requires: ").AppendLine(text);
        }
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

    private static string? RenderQualifier(
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
        var hasOld = TryGet(oldRecord, field, out var oldFieldValue) && !IsEmptyValue(oldFieldValue);
        var hasNew = TryGet(newRecord, field, out var newFieldValue) && !IsEmptyValue(newFieldValue);
        var oldValue = hasOld ? RenderValue(oldFieldValue, oldTranslations, context) : "none";
        var newValue = hasNew ? RenderValue(newFieldValue, newTranslations, context) : "none";
        if (oldValue is null || newValue is null || oldValue == newValue) return;

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
        var oldNotes = TryGet(oldRecord, "Notes", out var oldValue) && !IsEmptyValue(oldValue)
            ? RenderNotesValue(oldValue, oldTranslations, context)
            : "none";
        var newNotes = TryGet(newRecord, "Notes", out var newValue) && !IsEmptyValue(newValue)
            ? RenderNotesValue(newValue, newTranslations, context)
            : "none";
        if (oldNotes is null || newNotes is null || oldNotes == newNotes) return;

        markdown.Append("- Crafting category or notes: ").Append(newNotes).Append(" (was ").Append(oldNotes)
            .AppendLine(").");
    }

    private static string? RenderNotesValue(JsonElement value, RecipeTranslations translations, RenderContext context)
    {
        if (value.ValueKind != JsonValueKind.Array) return RenderValue(value, translations, context);
        var notes = value.EnumerateArray().Select(note => RenderValue(note, translations, context)).ToArray();
        return notes.Any(static note => note is null) ? null : string.Join(" / ", notes);
    }

    private static void AppendEnabledChange(StringBuilder markdown, JsonElement oldRecord, JsonElement newRecord)
    {
        if (SameField(oldRecord, newRecord, "Enabled")) return;
        if (TryGet(newRecord, "Enabled", out var enabled) &&
            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            markdown.AppendLine(enabled.GetBoolean() ? "- Recipe was enabled." : "- Recipe was disabled.");
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
            : null;
        var newValue = TryGet(newRecord, field, out var newFieldValue)
            ? RenderValue(newFieldValue, newTranslations, context)
            : null;
        if (oldValue is null || newValue is null || oldValue == newValue) return;

        markdown.Append("- ").Append(label).Append(": ").Append(newValue).Append(" (was ").Append(oldValue)
            .AppendLine(").");
    }

    /// <summary>The readable player-facing value, or null when it is empty or cannot be translated.</summary>
    private static string? RenderValue(JsonElement value, RecipeTranslations translations, RenderContext context)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            (TryGet(value, "key", out _) || TryGet(value, "nameKey", out _)))
        {
            var cacheKey = ItemPatchNotesMarkdown.CanonicalJson(value);
            if (context.RenderedLines.TryGetValue((translations, cacheKey), out var cached)) return cached;
            var rendered = ItemPatchNotesMarkdown.RenderPropertyLineText(
                value, translations, context.PropertyState, translations.VerifiedFlagKeys,
                translations.VerifiedTrailingValueKeys);
            context.RenderedLines.Add((translations, cacheKey), rendered);
            return rendered;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().Select(item => RenderValue(item, translations, context)).ToArray();
            return items.Length == 0 || items.Any(static item => item is null) ? null : string.Join(", ", items);
        }

        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) return null;
        return ItemPatchNotesMarkdown.LightEscape(value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : RawJson(value));
    }

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
        public ItemPatchNotesMarkdown.RenderState PropertyState { get; } = new() { ConciseRecipeText = true };

        public Dictionary<(RecipeTranslations Translations, string CanonicalLine), string?> RenderedLines { get; } = [];
    }

    private sealed record MigrationPairs(HashSet<string> Added, HashSet<string> Removed);

    private sealed record RecipeEntry(
        RecipeChangeKind Kind,
        string Key,
        int? RowIndex,
        JsonElement Record,
        JsonElement OldRecord,
        JsonElement NewRecord,
        string CategoryKey,
        string Category,
        string Title);

    private enum RecipeChangeKind
    {
        Modified,
        Added,
        Removed
    }
}
