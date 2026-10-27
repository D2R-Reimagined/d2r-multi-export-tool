// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using D2RMultiExport.Lib.Translation;
using static D2RMultiExport.Lib.Comparison.ComparisonMarkdown;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Renders concise player-facing item patch notes from the verified comparison report.</summary>
internal static class ItemPatchNotesMarkdown
{
    /// <summary>The item categories and presentation order used in the patch notes.</summary>
    private static readonly CategoryDefinition[] CategoryDefinitions =
    [
        new("uniques", "Uniques"),
        new("sets", "Sets"),
        new("runewords", "Runewords"),
        new("armors", "Base armor"),
        new("weapons", "Base weapons"),
        new("magicprefix", "Magic prefixes"),
        new("magicsuffix", "Magic suffixes")
    ];

    private static readonly HashSet<string> PropertyArrayFields = new(StringComparer.Ordinal)
    {
        "Lines", "PartialBonuses", "FullBonuses", "SetBonuses"
    };

    /// <summary>Exporter bookkeeping fields that do not help players read an added or removed item.</summary>
    private static readonly HashSet<string> NonPlayerMetadataFields = new(StringComparer.Ordinal)
    {
        "Vanilla", "Enabled", "DamageArmorEnhanced", "EquipmentType", "Code", "Group", "PType", "AutoPrefix"
    };

    /// <summary>Identifier and exporter bookkeeping fields whose changes are not gameplay changes.</summary>
    private static readonly HashSet<string> NonPlayerChangeFields = new(StringComparer.Ordinal)
    {
        "Index", "Vanilla", "DamageArmorEnhanced", "EquipmentType", "Code", "Group", "PType", "AutoPrefix"
    };

    public static async Task<string> RenderAsync(ComparisonReport report)
    {
        var oldTranslations = await RecipeTranslations.LoadAsync(report.OldSnapshot);
        var newTranslations = await RecipeTranslations.LoadAsync(report.NewSnapshot);
        var state = new RenderState();
        var sections = new StringBuilder();
        foreach (var definition in CategoryDefinitions)
        {
            var category = report.Categories.FirstOrDefault(item => item.Category == definition.Id);
            AppendCategory(sections, category, definition, oldTranslations, newTranslations, state);
        }

        var markdown = new StringBuilder();
        markdown.AppendLine("# Item patch notes");
        markdown.AppendLine();
        AppendSummary(markdown, report);
        markdown.Append(sections);
        return markdown.ToString();
    }

    private static void AppendSummary(StringBuilder markdown, ComparisonReport report)
    {
        markdown.AppendLine("## Change counts");
        markdown.AppendLine();
        markdown.AppendLine("| Category | Added | Removed | Modified | Unchanged | Nested changes |");
        markdown.AppendLine("|---|---:|---:|---:|---:|---|");
        foreach (var definition in CategoryDefinitions)
        {
            var category = report.Categories.FirstOrDefault(item => item.Category == definition.Id);
            markdown.Append("| ").Append(EscapeTable(definition.Title)).Append(" | ");
            if (category?.Status != "Compared")
            {
                markdown.AppendLine("unavailable | unavailable | unavailable | unavailable | unavailable |");
                continue;
            }

            var counts = category.Counts;
            markdown.Append(Invariant(counts.Added)).Append(" | ").Append(Invariant(counts.Removed)).Append(" | ")
                .Append(Invariant(counts.Modified)).Append(" | ").Append(Invariant(counts.Unchanged)).Append(" | ");
            if (category.NestedCounts is { } nested)
                markdown.Append(Invariant(nested.Added)).Append(" added, ").Append(Invariant(nested.Removed))
                    .Append(" removed, ").Append(Invariant(nested.Modified)).Append(" modified");
            else
                markdown.Append("—");
            markdown.AppendLine(" |");
        }

        markdown.AppendLine();
    }

    private static void AppendCategory(
        StringBuilder markdown,
        CategoryComparison? category,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        markdown.Append("## ").AppendLine(definition.Title);
        markdown.AppendLine();
        if (category?.Status != "Compared")
        {
            markdown.AppendLine("*Changes for this category are not available.*");
            markdown.AppendLine();
            return;
        }

        var changes = new StringBuilder();
        if (definition.Id == "uniques")
            AppendUniqueChanges(changes, category, definition, oldTranslations, newTranslations, state);
        else if (definition.Id == "sets")
            AppendSetChanges(changes, category, definition, oldTranslations, newTranslations, state);
        else
        {
            AppendEntrySection(changes, "Added", category.AddedRecords, definition, newTranslations, state);
            AppendEntrySection(changes, "Removed", category.RemovedRecords, definition, oldTranslations, state);
            if (category.ModifiedRecords.Count > 0)
            {
                AppendDetailsStart(changes, $"Modified ({Invariant(category.ModifiedRecords.Count)})");
                foreach (var record in category.ModifiedRecords)
                    AppendModifiedRecord(changes, record, definition, oldTranslations, newTranslations, state, 4);
                AppendDetailsEnd(changes);
            }
        }

        if (category.AddedRecords.Count == 0 && category.RemovedRecords.Count == 0 &&
            category.ModifiedRecords.Count == 0)
        {
            changes.AppendLine("*No reported changes.*");
            changes.AppendLine();
        }

        var counts = category.Counts;
        AppendDetailsStart(markdown,
            $"{definition.Title} — {Invariant(counts.Added)} added, {Invariant(counts.Removed)} removed, " +
            $"{Invariant(counts.Modified)} modified");
        markdown.Append(changes);
        AppendDetailsEnd(markdown);
    }

    private static void AppendEntrySection(
        StringBuilder markdown,
        string heading,
        IReadOnlyList<RecordEntry> records,
        CategoryDefinition definition,
        RecipeTranslations translations,
        RenderState state)
    {
        if (records.Count == 0) return;

        AppendDetailsStart(markdown, $"{heading} ({Invariant(records.Count)})");
        foreach (var record in records)
            AppendEntry(markdown, record, definition, translations, state, 4);
        AppendDetailsEnd(markdown);
    }

    private static void AppendUniqueChanges(
        StringBuilder markdown,
        CategoryComparison category,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var records = category.AddedRecords.Select(record =>
                new UniqueChange("Added", record.Key, record.Record, newTranslations, record, null))
            .Concat(category.RemovedRecords.Select(record =>
                new UniqueChange("Removed", record.Key, record.Record, oldTranslations, record, null)))
            .Concat(category.ModifiedRecords.Select(record =>
                new UniqueChange("Modified", record.Key, record.New, newTranslations, null, record)))
            .ToArray();
        foreach (var group in records.GroupBy(record => GetEquipmentCategoryKey(record.Record))
                     .OrderBy(group => GetEquipmentCategoryLabel(group.Key, oldTranslations, newTranslations),
                         StringComparer.Ordinal)
                     .ThenBy(static group => group.Key, StringComparer.Ordinal))
        {
            var added = group.Count(static record => record.ChangeType == "Added");
            var removed = group.Count(static record => record.ChangeType == "Removed");
            var modified = group.Count(static record => record.ChangeType == "Modified");
            var changeCounts = new List<string>();
            if (added > 0) changeCounts.Add(Invariant(added) + " added");
            if (removed > 0) changeCounts.Add(Invariant(removed) + " removed");
            if (modified > 0) changeCounts.Add(Invariant(modified) + " modified");
            var label = GetEquipmentCategoryLabel(group.Key, oldTranslations, newTranslations);
            AppendDetailsStart(markdown,
                $"{label} ({Invariant(group.Count())} changed: {string.Join(", ", changeCounts)})");
            foreach (var changeType in new[] { "Added", "Removed", "Modified" })
            {
                var changes = group.Where(record => record.ChangeType == changeType)
                    .OrderBy(record => GetDisplayNameText(record.Record, record.Key, record.Translations, state,
                        countFallback: false), StringComparer.Ordinal)
                    .ThenBy(static record => record.Key, StringComparer.Ordinal)
                    .ToArray();
                if (changes.Length == 0) continue;
                AppendHeading(markdown, 4, changeType);
                markdown.AppendLine();
                foreach (var record in changes)
                {
                    if (record.Entry is not null)
                        AppendEntry(markdown, record.Entry, definition, record.Translations, state, 5);
                    else
                        AppendModifiedRecord(markdown, record.Modified!, definition, oldTranslations, newTranslations,
                            state, 5);
                }
            }

            AppendDetailsEnd(markdown);
        }
    }

    private static void AppendSetChanges(
        StringBuilder markdown,
        CategoryComparison category,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var records = category.AddedRecords.Select(record =>
                new SetChange("Added", record.Key, record.Record, record, null, newTranslations))
            .Concat(category.RemovedRecords.Select(record =>
                new SetChange("Removed", record.Key, record.Record, record, null, oldTranslations)))
            .Concat(category.ModifiedRecords.Select(record =>
                new SetChange("Modified", record.Key, record.New, null, record, newTranslations)))
            .OrderBy(record => GetDisplayNameText(record.Record, record.Key, record.Translations, state,
                countFallback: false), StringComparer.Ordinal)
            .ThenBy(static record => record.Key, StringComparer.Ordinal)
            .ThenBy(static record => record.ChangeType, StringComparer.Ordinal);

        foreach (var record in records)
        {
            var displayName = GetDisplayNameText(record.Record, record.Key, record.Translations, state);
            var summary = displayName + " — " + record.ChangeType.ToLowerInvariant() + " set";
            if (record.Modified is not null)
            {
                var changedPieces = CountChangedSetPieces(record.Modified.NestedChanges);
                if (changedPieces > 0)
                    summary += $" ({Invariant(changedPieces)} changed {(changedPieces == 1 ? "piece" : "pieces")})";
            }
            else if (TryGet(record.Record, "SetItems", out var pieces) && pieces.ValueKind == JsonValueKind.Array)
            {
                var pieceCount = pieces.GetArrayLength();
                summary += $" ({Invariant(pieceCount)} piece{(pieceCount == 1 ? "" : "s")})";
            }

            AppendDetailsStart(markdown, summary);
            if (record.Entry is not null)
            {
                AppendVariantNames(markdown, record.Entry.VariantTypes, record.Translations);
                AppendCompleteRecord(markdown, record.Record, definition.Id, record.Translations, state);
            }
            else
            {
                var modified = record.Modified!;
                AppendChangedMetadata(markdown, modified, oldTranslations, newTranslations, state);
                AppendPropertyChanges(markdown, modified.Old, modified.New, oldTranslations, newTranslations, state,
                    "Partial Set Bonuses", "PartialBonuses");
                AppendPropertyChanges(markdown, modified.Old, modified.New, oldTranslations, newTranslations, state,
                    "Full Set Bonuses", "FullBonuses");
                if (modified.NestedChanges is { } nested)
                    AppendNestedChanges(markdown, nested, definition.Id, oldTranslations, newTranslations, state);
            }

            markdown.AppendLine();
            AppendDetailsEnd(markdown);
        }
    }

    private static int CountChangedSetPieces(NestedRecordChanges? changes) => changes is null
        ? 0
        : changes.Added.Count + changes.Removed.Count + changes.Modified.Count;

    private static void AppendEntry(
        StringBuilder markdown,
        RecordEntry record,
        CategoryDefinition definition,
        RecipeTranslations translations,
        RenderState state,
        int headingLevel)
    {
        var displayName = definition.Id == "uniques"
            ? EscapeHtml(GetDisplayNameText(record.Record, record.Key, translations, state))
            : GetDisplayName(record.Record, record.Key, translations, state);
        AppendHeading(markdown, headingLevel, displayName);
        markdown.AppendLine();
        if (definition.Id == "uniques") AppendUniqueTierDetails(markdown, record.Record, translations);
        if (definition.Id is not ("magicprefix" or "magicsuffix"))
            AppendVariantNames(markdown, record.VariantTypes, translations);
        AppendCompleteRecord(markdown, record.Record, definition.Id, translations, state);
        markdown.AppendLine();
    }

    private static void AppendUniqueTierDetails(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations)
    {
        if (!TryGet(record, "Equipment", out var equipment) || equipment.ValueKind != JsonValueKind.Object) return;

        var tiers = new (string Field, string Name)[]
            {
                ("NormCode", "Normal"), ("UberCode", "Exceptional"), ("UltraCode", "Elite")
            }.Select(tier => (tier.Name, Code: TryGetString(equipment, tier.Field)))
            .Where(static tier => !string.IsNullOrWhiteSpace(tier.Code))
            .ToArray();
        if (tiers.Length == 0) return;

        var itemCode = TryGetString(record, "Code");
        var matchingTiers = tiers.Where(tier => string.Equals(tier.Code, itemCode,
            StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matchingTiers.Length == 1)
        {
            markdown.Append("- Item tier: ").AppendLine(matchingTiers[0].Name);
            return;
        }

        var tierNames = tiers
            .Select(tier => TryTranslate(tier.Code!, translations) is { } baseName ? tier.Name + " " + baseName : null)
            .OfType<string>()
            .ToArray();
        if (tierNames.Length > 0) markdown.Append("- Base item tiers: ").AppendLine(string.Join(", ", tierNames));
    }

    private static void AppendModifiedRecord(
        StringBuilder markdown,
        ModifiedRecord record,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state,
        int headingLevel)
    {
        var displayName = definition.Id == "uniques"
            ? EscapeHtml(GetDisplayNameText(record.New, record.Key, newTranslations, state))
            : GetDisplayName(record.New, record.Key, newTranslations, state);
        AppendHeading(markdown, headingLevel, displayName);
        markdown.AppendLine();
        if (definition.Id == "uniques") AppendUniqueTierDetails(markdown, record.New, newTranslations);
        AppendModifiedRecordDetails(markdown, record, definition, oldTranslations, newTranslations, state);
        markdown.AppendLine();
    }

    private static void AppendModifiedRecordDetails(
        StringBuilder markdown,
        ModifiedRecord record,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        if (definition.Id is "magicprefix" or "magicsuffix")
            AppendAffixIdentity(markdown, record.New, newTranslations, state);
        else
            AppendVariantNames(markdown, record.VariantTypes, newTranslations);

        AppendChangedMetadata(markdown, record, oldTranslations, newTranslations, state);
        AppendPropertyChanges(markdown, record.Old, record.New, oldTranslations, newTranslations, state);
        if (record.NestedChanges is { } nested)
            AppendNestedChanges(markdown, nested, definition.Id, oldTranslations, newTranslations, state);
    }

    private static void AppendCompleteRecord(
        StringBuilder markdown,
        JsonElement record,
        string category,
        RecipeTranslations translations,
        RenderState state)
    {
        AppendMetadataTree(markdown, record, translations, state, 0, true);
        AppendAllPropertyCollections(markdown, record, translations, state);
        if (category == "sets" && TryGet(record, "SetItems", out var pieces))
            AppendAllSetPieces(markdown, pieces, translations, state);
        if (category is "armors" or "weapons" && TryGet(record, "AutoMagicGroups", out var groups))
            AppendAllAutomagicGroups(markdown, groups, translations, state);
    }

    private static void AppendMetadataTree(
        StringBuilder markdown,
        JsonElement value,
        RecipeTranslations translations,
        RenderState state,
        int depth,
        bool root)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            if (RenderValue(value, "", translations, state) is { } text)
                markdown.Append(new string(' ', depth * 2)).Append("- ").AppendLine(text);
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            var name = property.Name;
            if (root && name is "NameKey" or "Name" or "Index") continue;
            if (root && NonPlayerMetadataFields.Contains(name)) continue;
            if (name == "Index") continue;
            if (root && name == "Rarity" && TryGet(value, "Runes", out _)) continue;
            if (root && name == "Type" && TryGet(value, "Equipment", out _)) continue;
            if (name == "NameKey") continue;
            if ((name is "MaxLevel" or "ClassLevelReq") && property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt32(out var defaultValue) && defaultValue == 0) continue;
            if (name == "ETypes" && property.Value.ValueKind == JsonValueKind.Array &&
                property.Value.GetArrayLength() == 0) continue;
            if (property.Value.ValueKind == JsonValueKind.String &&
                string.IsNullOrEmpty(property.Value.GetString()) && name != "RequiredClass") continue;
            if (PropertyArrayFields.Contains(name) && property.Value.ValueKind == JsonValueKind.Array) continue;
            if (name == "DamageTypes" && IsKeyedLineArray(property.Value)) continue;
            if (name == "SetItems" || name == "AutoMagicGroups") continue;

            if (name == "Equipment" && property.Value.ValueKind == JsonValueKind.Object)
            {
                AppendEquipmentMetadata(markdown, property.Value, translations, state, depth);
                continue;
            }

            if (name == "Runes" && property.Value.ValueKind == JsonValueKind.Array)
            {
                AppendRuneSequence(markdown, property.Value, translations, state, depth);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                AppendMetadataTree(markdown, property.Value, translations, state, depth + 1, false);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                if (name == "Types" && property.Value.GetArrayLength() > 0 &&
                    property.Value.EnumerateArray().All(static item => item.ValueKind == JsonValueKind.Object))
                {
                    foreach (var variant in property.Value.EnumerateArray())
                    {
                        markdown.Append(new string(' ', depth * 2)).Append("- Variant: ")
                            .AppendLine(RenderVariantName(variant, translations, state) ?? "Variant");
                        AppendMetadataTree(markdown, variant, translations, state, depth + 1, true);
                    }

                    continue;
                }

                AppendMetadataArray(markdown, name, property.Value, translations, state, depth);
                continue;
            }

            if (RenderValue(property.Value, name, translations, state) is not { } rendered) continue;
            markdown.Append(new string(' ', depth * 2)).Append("- ").Append(FormatFieldName(name)).Append(": ")
                .AppendLine(rendered);
        }
    }

    private static void AppendEquipmentMetadata(
        StringBuilder markdown,
        JsonElement equipment,
        RecipeTranslations translations,
        RenderState state,
        int depth)
    {
        var nameKey = TryGetString(equipment, "NameKey");
        var code = TryGetString(equipment, "Code") ?? TryGetString(equipment, "NormCode");
        var baseItem = !string.IsNullOrWhiteSpace(nameKey) ? RenderNameKey(nameKey, translations, state) : null;
        if (baseItem is null && !string.IsNullOrWhiteSpace(code)) baseItem = TryTranslate(code, translations);
        if (baseItem is not null)
            markdown.Append(new string(' ', depth * 2)).Append("- Base Item: ").AppendLine(baseItem);

        foreach (var property in equipment.EnumerateObject())
        {
            if (property.Name is "NameKey" or "Code" or "NormCode" or "ExCode" or "UberCode" or "UltraCode") continue;
            if (NonPlayerMetadataFields.Contains(property.Name)) continue;
            if (PropertyArrayFields.Contains(property.Name) &&
                property.Value.ValueKind == JsonValueKind.Array) continue;
            if (property.Name == "DamageTypes" && IsKeyedLineArray(property.Value)) continue;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                AppendMetadataTree(markdown, property.Value, translations, state, depth + 1, false);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                AppendMetadataArray(markdown, property.Name, property.Value, translations, state, depth);
                continue;
            }

            if (RenderValue(property.Value, property.Name, translations, state) is not { } rendered) continue;
            markdown.Append(new string(' ', depth * 2)).Append("- ").Append(FormatFieldName(property.Name)).Append(": ")
                .AppendLine(rendered);
        }
    }

    private static void AppendMetadataArray(
        StringBuilder markdown,
        string name,
        JsonElement values,
        RecipeTranslations translations,
        RenderState state,
        int depth)
    {
        var label = FormatFieldName(name);
        if (values.GetArrayLength() == 0)
            return;

        var items = values.EnumerateArray().ToArray();
        if (items.All(static item => item.ValueKind is JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or
                JsonValueKind.False))
        {
            var rendered = items.Select(item => RenderValue(item, name, translations, state)).OfType<string>()
                .ToArray();
            if (rendered.Length > 0)
                markdown.Append(new string(' ', depth * 2)).Append("- ").Append(label).Append(": ")
                    .AppendLine(string.Join(", ", rendered));
            return;
        }

        var entries = new StringBuilder();
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].ValueKind == JsonValueKind.Object)
            {
                var details = new StringBuilder();
                AppendMetadataTree(details, items[index], translations, state, depth + 2, false);
                if (details.Length == 0) continue;
                var itemLabel = name == "DamageTypes" ? "Damage type" : label;
                entries.Append(new string(' ', (depth + 1) * 2)).Append("- ").Append(itemLabel).Append(' ')
                    .Append(Invariant(index + 1)).AppendLine();
                entries.Append(details);
            }
            else if (RenderValue(items[index], name, translations, state) is { } rendered)
                entries.Append(new string(' ', (depth + 1) * 2)).Append("- ").AppendLine(rendered);
        }

        if (entries.Length == 0) return;
        markdown.Append(new string(' ', depth * 2)).Append("- ").Append(label).AppendLine(":");
        markdown.Append(entries);
    }

    private static void AppendRuneSequence(
        StringBuilder markdown,
        JsonElement runes,
        RecipeTranslations translations,
        RenderState state,
        int depth)
    {
        if (RenderRuneSequence(runes, translations, state) is { } sequence)
            markdown.Append(new string(' ', depth * 2)).Append("- Rune sequence: ").AppendLine(sequence);
    }

    /// <summary>The readable rune sequence, or null when any rune cannot be named.</summary>
    private static string? RenderRuneSequence(
        JsonElement runes,
        RecipeTranslations translations,
        RenderState state)
    {
        if (runes.ValueKind != JsonValueKind.Array) return RenderValue(runes, "Runes", translations, state);
        var values = runes.EnumerateArray().Select(rune =>
        {
            if (rune.ValueKind == JsonValueKind.Object && TryGetString(rune, "NameKey") is { } nameKey)
                return RenderNameKey(nameKey, translations, state);
            var code = rune.ValueKind == JsonValueKind.Object
                ? TryGetString(rune, "Code")
                : rune.ValueKind == JsonValueKind.String
                    ? rune.GetString()
                    : null;
            return code is null
                ? RenderValue(rune, "", translations, state)
                : TryTranslate(code, translations);
        }).ToArray();
        return values.Length == 0 || values.Any(static value => value is null) ? null : string.Join(" → ", values);
    }

    private static void AppendAllPropertyCollections(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderState state)
    {
        var collections = GetPropertyCollections(record)
            .Where(static collection => collection.Lines.GetArrayLength() > 0)
            .ToList();
        if (collections.Count == 0) return;

        var properties = new StringBuilder();
        foreach (var collection in collections)
        {
            var label = PropertyCollectionLabel(collection.Path, translations);
            var lines = new StringBuilder();
            foreach (var line in collection.Lines.EnumerateArray())
                AppendPropertyLine(lines, line, translations, state, label.Length == 0 ? 1 : 2);
            if (lines.Length == 0) continue;
            if (label.Length > 0) properties.Append("  - ").Append(LightEscape(label)).AppendLine(":");
            properties.Append(lines);
        }

        if (properties.Length == 0) return;
        markdown.AppendLine("- Properties:");
        markdown.Append(properties);
    }

    private static void AppendPropertyChanges(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state,
        string? sectionHeading = null,
        string? onlyPath = null)
    {
        var oldCollections = GetPropertyCollections(oldRecord).ToDictionary(static collection => collection.Path,
            StringComparer.Ordinal);
        var newCollections = GetPropertyCollections(newRecord).ToDictionary(static collection => collection.Path,
            StringComparer.Ordinal);
        var paths = oldCollections.Keys.Concat(newCollections.Keys).Distinct(StringComparer.Ordinal)
            .Where(path => onlyPath is null || path == onlyPath).ToList();
        var emittedHeading = false;
        foreach (var path in paths)
        {
            oldCollections.TryGetValue(path, out var oldCollection);
            newCollections.TryGetValue(path, out var newCollection);
            var oldLines = oldCollection is null ? [] : oldCollection.Lines.EnumerateArray().ToArray();
            var newLines = newCollection is null ? [] : newCollection.Lines.EnumerateArray().ToArray();
            var diff = DiffProperties(oldLines, newLines, oldTranslations, newTranslations, state);
            if (diff.Changes.Count == 0) continue;
            if (!emittedHeading)
            {
                if (sectionHeading is null)
                    markdown.AppendLine("- Property changes:");
                else
                    markdown.Append("#### ").AppendLine(sectionHeading);
                emittedHeading = true;
            }

            if (sectionHeading is null && (paths.Count > 1 || path.Length > 0))
                markdown.Append("  - ").Append(LightEscape(PropertyCollectionLabel(path, newTranslations)))
                    .AppendLine(":");
            foreach (var change in diff.Changes)
                markdown.Append(sectionHeading is null ? "    - " : "- ").AppendLine(change);
        }
    }

    internal static PropertyDiff DiffProperties(
        IReadOnlyList<JsonElement> oldLines,
        IReadOnlyList<JsonElement> newLines,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var oldMatched = new bool[oldLines.Count];
        var newMatched = new bool[newLines.Count];
        var newByExact = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var index = 0; index < newLines.Count; index++)
        {
            var exact = CanonicalJson(newLines[index]);
            if (!newByExact.TryGetValue(exact, out var indexes))
                newByExact.Add(exact, indexes = new Queue<int>());
            indexes.Enqueue(index);
        }

        for (var index = 0; index < oldLines.Count; index++)
        {
            var exact = CanonicalJson(oldLines[index]);
            if (!newByExact.TryGetValue(exact, out var indexes) || indexes.Count == 0) continue;
            oldMatched[index] = true;
            newMatched[indexes.Dequeue()] = true;
        }

        var oldByIdentity = BuildIdentityBuckets(oldLines, oldMatched);
        var newByIdentity = BuildIdentityBuckets(newLines, newMatched);
        var changes = new List<string>();
        foreach (var (identity, oldIndexes) in oldByIdentity)
        {
            if (oldIndexes.Count != 1 || !newByIdentity.TryGetValue(identity, out var newIndexes) ||
                newIndexes.Count != 1)
                continue;
            var oldIndex = oldIndexes[0];
            var newIndex = newIndexes[0];
            oldMatched[oldIndex] = true;
            newMatched[newIndex] = true;
            var oldText = RenderPropertyLineText(oldLines[oldIndex], oldTranslations, state);
            var newText = RenderPropertyLineText(newLines[newIndex], newTranslations, state);
            if (newText is null || newText == oldText) continue;
            changes.Add(oldText is null
                ? $"Changed: {newText}"
                : $"Changed: {newText} (was {TrimCommonSuffix(oldText, newText)})");
        }

        foreach (var index in Enumerable.Range(0, newLines.Count).Where(index => !newMatched[index]))
        {
            if (!IsEmptyPropertyLine(newLines[index]) &&
                RenderPropertyLineText(newLines[index], newTranslations, state) is { } text)
                changes.Add($"Added: {text}");
        }

        foreach (var index in Enumerable.Range(0, oldLines.Count).Where(index => !oldMatched[index]))
        {
            if (!IsEmptyPropertyLine(oldLines[index]) &&
                RenderPropertyLineText(oldLines[index], oldTranslations, state) is { } text)
                changes.Add($"Removed: {text}");
        }

        return new PropertyDiff(changes);
    }

    private static bool IsEmptyPropertyLine(JsonElement line) =>
        line.ValueKind == JsonValueKind.Array && line.GetArrayLength() == 0;

    private static Dictionary<string, List<int>> BuildIdentityBuckets(IReadOnlyList<JsonElement> lines, bool[] matched)
    {
        var buckets = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Count; index++)
        {
            if (matched[index] || !TryPropertyIdentity(lines[index], out var identity)) continue;
            if (!buckets.TryGetValue(identity, out var indexes)) buckets.Add(identity, indexes = []);
            indexes.Add(index);
        }

        return buckets;
    }

    private static bool TryPropertyIdentity(JsonElement line, out string identity)
    {
        identity = "";
        if (line.ValueKind != JsonValueKind.Object || TryGet(line, "children", out _)) return false;
        var key = TryGetString(line, "key") ?? TryGetString(line, "nameKey");
        if (string.IsNullOrEmpty(key)) return false;

        var stable = new StringBuilder();
        stable.Append("key=").Append(CanonicalString(key)).Append(';');
        var hasArgs = TryGet(line, "args", out var args);
        foreach (var property in line.EnumerateObject().Where(static property => property.Name != "args")
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
            stable.Append(property.Name).Append('=').Append(CanonicalJson(property.Value)).Append(';');
        if (hasArgs)
        {
            if (args.ValueKind == JsonValueKind.Array)
            {
                var nonNumericArgs = args.EnumerateArray()
                    .Where(static argument => argument.ValueKind != JsonValueKind.Number).ToArray();
                if (nonNumericArgs.Length > 0)
                {
                    stable.Append("args=[");
                    foreach (var argument in nonNumericArgs)
                        stable.Append(CanonicalJson(argument)).Append(';');
                    stable.Append("];");
                }
            }
            else
                stable.Append("args=").Append(CanonicalJson(args)).Append(';');
        }

        identity = stable.ToString();
        return true;
    }

    /// <summary>
    /// The readable player-facing text of a keyed property line, or null when its template, value or
    /// restriction cannot be rendered safely. Unrendered lines stay in the detailed comparison reports.
    /// </summary>
    internal static string? RenderPropertyLineText(
        JsonElement line,
        RecipeTranslations translations,
        RenderState state,
        IReadOnlySet<string>? verifiedFlagKeys = null,
        IReadOnlySet<string>? verifiedTrailingValueKeys = null)
    {
        if (line.ValueKind != JsonValueKind.Object) return RenderValue(line, "", translations, state);
        verifiedFlagKeys ??= translations.VerifiedFlagKeys;
        verifiedTrailingValueKeys ??= translations.VerifiedTrailingValueKeys;
        var key = TryGetString(line, "key");
        if (string.IsNullOrEmpty(key)) key = TryGetString(line, "nameKey");
        key ??= "";
        var args = TryGet(line, "args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
            ? argsElement.EnumerateArray().ToArray()
            : [];
        string rendered;
        var hasTemplate = false;
        var template = "";
        if (key.Length > 0) hasTemplate = translations.TryGet(key, out template);
        if (hasTemplate && ComparisonMarkdown.TryFormatTemplate(template, args, translations, out var formatted))
            rendered = formatted;
        else if (hasTemplate && TryFormatRangeTemplate(template, args, translations, out formatted))
            rendered = formatted;
        else if (hasTemplate && !template.Contains('%'))
        {
            var isVerifiedFixedFlag = verifiedFlagKeys.Contains(key) && args.Length == 1 &&
                                      args[0].ValueKind == JsonValueKind.Number &&
                                      args[0].TryGetInt32(out var flagValue) && flagValue == 1;
            var trailingValue = 0;
            var hasVerifiedTrailingValue = verifiedTrailingValueKeys.Contains(key) && args.Length == 1 &&
                                           args[0].ValueKind == JsonValueKind.Number &&
                                           args[0].TryGetInt32(out trailingValue) && trailingValue > 1;
            if (args.Length > 0 && !isVerifiedFixedFlag && !hasVerifiedTrailingValue)
            {
                state.UntranslatedValues++;
                return null;
            }

            rendered = hasVerifiedTrailingValue ? template + " +" + trailingValue : template;
        }
        else
        {
            state.UntranslatedValues++;
            return null;
        }

        rendered = CollapseNumericRangeParentheses(rendered);
        var annotations = new List<string>();
        var perLevelSuffix = "";
        foreach (var property in line.EnumerateObject())
        {
            // Structural metadata: group identity and pick rules, child weights and random-selection ranges.
            if (property.Name is "key" or "nameKey" or "args" or "children" or "selection" or "code" or
                "pickMode" or "chance") continue;
            if (property.Name == "classOnly" && property.Value.ValueKind == JsonValueKind.String)
            {
                if (!translations.TryGet(property.Value.GetString() ?? "", out var classText))
                {
                    state.UntranslatedValues++;
                    return null;
                }

                annotations.Add(RemoveOuterParentheses(classText));
                continue;
            }

            if (property.Name == "itemsRequired")
            {
                annotations.Add($"{RawJson(property.Value)} Items");
                continue;
            }

            if (property.Name == "fullSet" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                annotations.Add(property.Value.GetBoolean() ? "Full set bonus" : "Partial set bonus");
                continue;
            }

            if (property.Name == "qualifier" && property.Value.ValueKind == JsonValueKind.String)
            {
                var qualifier = property.Value.GetString() ?? "";
                if (translations.TryGet(qualifier, out var translatedQualifier))
                    annotations.Add(translatedQualifier);
                else if (qualifier.Equals("Weapon", StringComparison.OrdinalIgnoreCase) ||
                         qualifier.Equals("Armor", StringComparison.OrdinalIgnoreCase) ||
                         qualifier.Equals("Shield", StringComparison.OrdinalIgnoreCase))
                    annotations.Add(qualifier + " only");
                else
                {
                    state.UntranslatedValues++;
                    return null;
                }

                continue;
            }

            if (property.Name == "perLevel" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                if (property.Value.GetBoolean())
                    perLevelSuffix = translations.TryGet("strPerCharacterLevelSuffix", out var suffix)
                        ? suffix
                        : "per character level";
                continue;
            }

            state.UntranslatedValues++;
            return null;
        }

        if (state.ConciseRecipeText) annotations = annotations.Distinct(StringComparer.Ordinal).ToList();

        if (annotations.Count > 0)
            rendered += state.ConciseRecipeText && annotations.Count == 1 &&
                        annotations[0].StartsWith('(') && annotations[0].EndsWith(')')
                ? " " + annotations[0]
                : " (" + string.Join("; ", annotations) + ")";
        if (perLevelSuffix.Length > 0)
            rendered += (char.IsWhiteSpace(perLevelSuffix[0]) ? "" : " ") + perLevelSuffix;
        return LightEscape(rendered);
    }

    private static string RemoveOuterParentheses(string value)
    {
        if (value.Length < 2 || value[0] != '(' || value[^1] != ')') return value;

        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '(')
                depth++;
            else if (value[index] == ')')
            {
                depth--;
                if (depth < 0 || depth == 0 && index < value.Length - 1) return value;
            }
        }

        return depth == 0 ? value[1..^1] : value;
    }

    private static bool TryFormatRangeTemplate(
        string template,
        JsonElement[] args,
        RecipeTranslations translations,
        out string formatted)
    {
        formatted = "";
        var tokens = new List<(int Start, int Length, string Token)>();
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != '%') continue;
            if (index + 1 >= template.Length) return false;
            if (template[index + 1] == '%')
            {
                index++;
                continue;
            }

            string token;
            var tokenLength = 2;
            if (template[index + 1] == '+' && index + 2 < template.Length && template[index + 2] == 'd')
            {
                token = "+d";
                tokenLength = 3;
            }
            else if (template[index + 1] is 'd' or 'i' or 's')
                token = template[index + 1].ToString();
            else
                return false;

            tokens.Add((index, tokenLength, token));
            index += tokenLength - 1;
        }

        var numericTokens = tokens
            .Select((token, index) => (token, index))
            .Where(static entry => entry.token.Token is "d" or "+d" or "i")
            .ToArray();
        if (numericTokens.Length != 1 || args.Length != tokens.Count + 1) return false;

        var (rangeToken, rangeIndex) = numericTokens[0];
        if (args[rangeIndex].ValueKind != JsonValueKind.Number ||
            args[rangeIndex + 1].ValueKind != JsonValueKind.Number)
            return false;

        var minimum = RawJson(args[rangeIndex]);
        var maximum = RawJson(args[rangeIndex + 1]);
        var equal = CanonicalJson(args[rangeIndex])
            .Equals(CanonicalJson(args[rangeIndex + 1]), StringComparison.Ordinal);
        var range = rangeToken.Token == "+d" ? "+" : "";
        range += equal ? minimum : $"({minimum}-{maximum})";

        var rangeTemplate = new StringBuilder(template.Length + range.Length);
        var cursor = 0;
        foreach (var (token, index) in tokens.Select((token, index) => (token, index)))
        {
            rangeTemplate.Append(template.AsSpan(cursor, token.Start - cursor));
            if (index == rangeIndex)
                rangeTemplate.Append(range);
            else
                rangeTemplate.Append(template.AsSpan(token.Start, token.Length));
            cursor = token.Start + token.Length;
        }

        rangeTemplate.Append(template.AsSpan(cursor));
        var remainingArgs = args.Where((_, index) => index != rangeIndex && index != rangeIndex + 1).ToArray();
        return ComparisonMarkdown.TryFormatTemplate(rangeTemplate.ToString(), remainingArgs, translations,
            out formatted);
    }

    internal static void AppendPropertyLine(
        StringBuilder markdown,
        JsonElement line,
        RecipeTranslations translations,
        RenderState state,
        int depth)
    {
        if (RenderPropertyLineText(line, translations, state) is not { } text) return;
        markdown.Append(new string(' ', depth * 2)).Append("- ").AppendLine(text);
        if (!TryGet(line, "children", out var children) || children.ValueKind != JsonValueKind.Array) return;
        foreach (var child in children.EnumerateArray())
            AppendPropertyLine(markdown, child, translations, state, depth + 1);
    }

    private static void AppendChangedMetadata(
        StringBuilder markdown,
        ModifiedRecord record,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var runeSequenceChanged = record.Changes.Any(change => IsRuneSequencePath(change.Path));
        var damageTypePropertiesChanged = HasReadableDamageTypePropertyChanges(record.Old, record.New);
        var changes = record.Changes.Where(change => !IsPropertyChangePath(change.Path) &&
                                                     !IsRuneSequencePath(change.Path) &&
                                                     !NonPlayerChangeFields.Contains(LastPathName(change.Path)) &&
                                                     !(damageTypePropertiesChanged &&
                                                       IsDamageTypesMetadataPath(change.Path)) &&
                                                     (record.NestedChanges is null ||
                                                      !IsNestedPath(change.Path, record.NestedChanges.Field)))
            .OrderBy(change => IsKnownFieldPath(change.Path) ? 0 : 1)
            .ToArray();
        var details = new StringBuilder();
        foreach (var change in changes)
            AppendFieldChange(details, change, oldTranslations, newTranslations, state, "  ");
        if (runeSequenceChanged)
        {
            var oldRunes = TryGet(record.Old, "Runes", out var oldRuneValues)
                ? RenderRuneSequence(oldRuneValues, oldTranslations, state)
                : null;
            var newRunes = TryGet(record.New, "Runes", out var newRuneValues)
                ? RenderRuneSequence(newRuneValues, newTranslations, state)
                : null;
            if (newRunes is not null)
            {
                details.Append("  - Rune sequence: ").Append(newRunes);
                if (oldRunes is not null) details.Append(" (was ").Append(oldRunes).Append(')');
                details.AppendLine();
            }
        }

        if (details.Length == 0) return;
        markdown.AppendLine("- Changed details:");
        markdown.Append(details);
    }

    private static bool HasReadableDamageTypePropertyChanges(JsonElement oldRecord, JsonElement newRecord)
    {
        var oldCollections = GetPropertyCollections(oldRecord)
            .Where(static collection => IsDamageTypesPropertyPath(collection.Path))
            .ToDictionary(static collection => collection.Path, StringComparer.Ordinal);
        var newCollections = GetPropertyCollections(newRecord)
            .Where(static collection => IsDamageTypesPropertyPath(collection.Path))
            .ToDictionary(static collection => collection.Path, StringComparer.Ordinal);
        foreach (var path in oldCollections.Keys.Concat(newCollections.Keys).Distinct(StringComparer.Ordinal))
        {
            oldCollections.TryGetValue(path, out var oldCollection);
            newCollections.TryGetValue(path, out var newCollection);
            if (oldCollection is not null && newCollection is not null &&
                CanonicalJson(oldCollection.Lines).Equals(CanonicalJson(newCollection.Lines), StringComparison.Ordinal))
                continue;
            if (HasPropertyLineContent(oldCollection) || HasPropertyLineContent(newCollection)) return true;
        }

        return false;
    }

    private static bool HasPropertyLineContent(PropertyCollection? collection) =>
        collection is not null && collection.Lines.ValueKind == JsonValueKind.Array &&
        collection.Lines.EnumerateArray().Any(static line => !IsEmptyPropertyLine(line));

    private static bool IsDamageTypesPropertyPath(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment =>
                segment == "DamageTypes" || segment.StartsWith("DamageTypes[", StringComparison.Ordinal));

    private static bool IsDamageTypesMetadataPath(string path) => LastPathName(path) == "DamageTypes";

    private static void AppendFieldChange(
        StringBuilder markdown,
        FieldChange change,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state,
        string indent)
    {
        var label = FormatPathName(change.Path);
        var oldValue = change.OldValue is { } oldRaw
            ? RenderValue(oldRaw, LastPathName(change.Path), oldTranslations, state)
            : null;
        var newValue = change.NewValue is { } newRaw
            ? RenderValue(newRaw, LastPathName(change.Path), newTranslations, state)
            : null;
        if (change.OldValue is not null && oldValue is null || change.NewValue is not null && newValue is null ||
            oldValue == newValue) return;
        markdown.Append(indent).Append("- ");
        if (oldValue is null)
            markdown.Append("Added: ").Append(label).Append(": ").AppendLine(newValue);
        else if (newValue is null)
            markdown.Append("Removed: ").Append(label).Append(": ").AppendLine(oldValue);
        else
            markdown.Append(label).Append(": ").Append(newValue).Append(" (was ").Append(oldValue).AppendLine(")");
    }

    private static void AppendNestedChanges(
        StringBuilder markdown,
        NestedRecordChanges changes,
        string category,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var isAutomagic = category is "armors" or "weapons";
        if (!isAutomagic)
        {
            AppendSetPieceChanges(markdown, changes, oldTranslations, newTranslations, state);
            return;
        }

        if (changes.Added.Count == 0 && changes.Removed.Count == 0 && changes.Modified.Count == 0) return;
        markdown.AppendLine("- Eligible automagic affix changes (can roll; not guaranteed on every item):");
        AppendNestedEntries(markdown, "Added", changes.Added, newTranslations, state, category);
        AppendNestedEntries(markdown, "Removed", changes.Removed, oldTranslations, state, category);
        if (changes.Modified.Count > 0)
        {
            markdown.AppendLine("  - Modified:");
            foreach (var nested in changes.Modified)
            {
                AppendModifiedRecord(markdown, nested, new CategoryDefinition(category, category), oldTranslations,
                    newTranslations, state, 5);
            }
        }
    }

    private static void AppendSetPieceChanges(
        StringBuilder markdown,
        NestedRecordChanges changes,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var count = CountChangedSetPieces(changes);
        if (count == 0) return;
        markdown.Append("#### Changed Set Pieces (").Append(Invariant(count)).AppendLine(")");

        AppendNestedSetPieceEntries(markdown, "Added", changes.Added, newTranslations, state);
        AppendNestedSetPieceEntries(markdown, "Removed", changes.Removed, oldTranslations, state);
        foreach (var nested in changes.Modified
                     .OrderBy(record => GetDisplayNameText(record.New, record.Key, newTranslations, state,
                         countFallback: false), StringComparer.Ordinal)
                     .ThenBy(static record => record.Key, StringComparer.Ordinal))
        {
            AppendHeading(markdown, 5,
                EscapeHtml(GetDisplayNameText(nested.New, nested.Key, newTranslations, state)) + " — modified");
            markdown.AppendLine();
            AppendModifiedRecordDetails(markdown, nested, new CategoryDefinition("set-piece", "Set piece"),
                oldTranslations, newTranslations, state);
            markdown.AppendLine();
        }
    }

    private static void AppendNestedSetPieceEntries(
        StringBuilder markdown,
        string status,
        IReadOnlyList<RecordEntry> records,
        RecipeTranslations translations,
        RenderState state)
    {
        foreach (var record in records.OrderBy(record =>
                             GetDisplayNameText(record.Record, record.Key, translations, state, countFallback: false),
                         StringComparer.Ordinal)
                     .ThenBy(static record => record.Key, StringComparer.Ordinal))
        {
            AppendHeading(markdown, 5,
                EscapeHtml(GetDisplayNameText(record.Record, record.Key, translations, state)) + " — " +
                status.ToLowerInvariant());
            markdown.AppendLine();
            AppendCompleteRecord(markdown, record.Record, "set-piece", translations, state);
            markdown.AppendLine();
        }
    }

    private static void AppendNestedEntries(
        StringBuilder markdown,
        string heading,
        IReadOnlyList<RecordEntry> records,
        RecipeTranslations translations,
        RenderState state,
        string category)
    {
        if (records.Count == 0) return;
        markdown.Append("  - ").AppendLine(heading + ":");
        foreach (var record in records)
        {
            markdown.Append("    - ").AppendLine(GetDisplayName(record.Record, record.Key, translations, state));
            AppendVariantNames(markdown, record.VariantTypes, translations, 3);
            AppendCompleteRecord(markdown, record.Record, category, translations, state);
        }
    }

    private static void AppendAllSetPieces(
        StringBuilder markdown,
        JsonElement pieces,
        RecipeTranslations translations,
        RenderState state)
    {
        if (pieces.ValueKind != JsonValueKind.Array || pieces.GetArrayLength() == 0) return;
        markdown.AppendLine("- Set pieces:");
        foreach (var piece in pieces.EnumerateArray())
        {
            var key = TryGetString(piece, "Index") ?? "unknown piece";
            markdown.Append("  - ").AppendLine(EscapeHtml(GetDisplayNameText(piece, key, translations, state)));
            AppendCompleteRecord(markdown, piece, "set-piece", translations, state);
        }
    }

    private static void AppendAllAutomagicGroups(
        StringBuilder markdown,
        JsonElement groups,
        RecipeTranslations translations,
        RenderState state)
    {
        if (groups.ValueKind != JsonValueKind.Array || groups.GetArrayLength() == 0) return;
        markdown.AppendLine("- Possible automagic groups (not guaranteed rolls):");
        foreach (var group in groups.EnumerateArray())
        {
            var nameKey = TryGetString(group, "NameKey");
            var code = TryGetString(group, "Code");
            var name = (nameKey is not null ? RenderNameKey(nameKey, translations, state) : null)
                       ?? (code is not null ? TryTranslate(code, translations) ?? LightEscape(code) : null)
                       ?? "Unnamed group";
            markdown.Append("  - ").AppendLine(name);
            AppendCompleteRecord(markdown, group, "automagic", translations, state);
        }
    }

    private static List<PropertyCollection> GetPropertyCollections(JsonElement record)
    {
        var result = new List<PropertyCollection>();
        CollectPropertyCollections(record, "", result);
        return result;
    }

    private static void CollectPropertyCollections(JsonElement value, string path, List<PropertyCollection> result)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = path.Length == 0 ? property.Name : path + "." + property.Name;
                if (PropertyArrayFields.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    result.Add(new PropertyCollection(propertyPath, propertyPath, property.Value));
                    continue;
                }

                if (property.Name == "DamageTypes" && IsKeyedLineArray(property.Value))
                {
                    result.Add(new PropertyCollection(propertyPath, propertyPath, property.Value));
                    continue;
                }

                if (property.Name is "SetItems" or "AutoMagicGroups") continue;
                if (property.Value.ValueKind == JsonValueKind.Object)
                    CollectPropertyCollections(property.Value, propertyPath, result);
                else if (property.Value.ValueKind == JsonValueKind.Array && property.Name == "Types")
                {
                    var items = property.Value.EnumerateArray().ToArray();
                    for (var index = 0; index < items.Length; index++)
                        if (items[index].ValueKind == JsonValueKind.Object)
                        {
                            var variant = TryGetString(items[index], "Index") ?? Invariant(index + 1);
                            CollectPropertyCollections(items[index], propertyPath + "[" + variant + "]", result);
                        }
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    var items = property.Value.EnumerateArray().ToArray();
                    for (var index = 0; index < items.Length; index++)
                        if (items[index].ValueKind == JsonValueKind.Object)
                            CollectPropertyCollections(items[index], propertyPath + "[" + Invariant(index) + "]",
                                result);
                }
            }
        }
    }

    private static void AppendVariantNames(
        StringBuilder markdown,
        IReadOnlyList<string>? variants,
        RecipeTranslations translations,
        int indent = 0)
    {
        if (variants is not { Count: > 0 }) return;
        var names = variants.Select(variant => TryTranslate(variant, translations)).ToArray();
        if (names.Any(static name => name is null)) return;
        markdown.Append(new string(' ', indent * 2)).Append("- Equipment variants: ")
            .AppendLine(string.Join(", ", names));
    }

    private static void AppendAffixIdentity(
        StringBuilder markdown,
        JsonElement record,
        RecipeTranslations translations,
        RenderState state)
    {
        var fields = new List<string>();
        AppendAffixIdentityField(fields, record, "Level", "Level", translations, state);
        if (!TryGet(record, "ReqLevel", out _) && !TryGet(record, "RequiredLevel", out _))
            AppendAffixIdentityField(fields, record, "LevelReq", "Required Level", translations, state);
        else if (TryGet(record, "ReqLevel", out _))
            AppendAffixIdentityField(fields, record, "ReqLevel", "Required Level", translations, state);
        else
            AppendAffixIdentityField(fields, record, "RequiredLevel", "Required Level", translations, state);
        AppendAffixIdentityField(fields, record, "Types", FormatFieldName("Types"), translations, state);
        AppendAffixIdentityField(fields, record, "ETypes", FormatFieldName("ETypes"), translations, state);
        AppendAffixIdentityField(fields, record, "Class", "Class", translations, state);
        if (fields.Count > 0) markdown.Append("- Affix variant: ").AppendLine(string.Join("; ", fields));
    }

    private static void AppendAffixIdentityField(
        ICollection<string> fields,
        JsonElement record,
        string field,
        string label,
        RecipeTranslations translations,
        RenderState state)
    {
        if (!TryGet(record, field, out var value)) return;
        if (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) return;
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) return;
        if (RenderValue(value, field, translations, state) is { } rendered) fields.Add(label + ": " + rendered);
    }

    private static string GetDisplayName(
        JsonElement record,
        string fallback,
        RecipeTranslations translations,
        RenderState state,
        bool countFallback = true) =>
        LightEscape(GetDisplayNameText(record, fallback, translations, state, countFallback));

    private static string GetDisplayNameText(
        JsonElement record,
        string fallback,
        RecipeTranslations translations,
        RenderState state,
        bool countFallback = true)
    {
        var nameKey = TryGetString(record, "NameKey");
        if (nameKey is not null)
        {
            if (translations.TryGet(nameKey, out var localizedName)) return localizedName;
            if (countFallback) state.UntranslatedValues++;
        }

        if (TryGetString(record, "Name") is { Length: > 0 } name) return name;
        if (TryGetString(record, "Index") is { } index)
            return translations.TryGet(index, out var localized) ? localized : index;
        if (TryGetString(record, "Code") is { } code) return code;
        return fallback;
    }

    private static string GetEquipmentCategoryKey(JsonElement record)
    {
        var type = TryGet(record, "Equipment", out var equipment) ? TryGetString(equipment, "Type") : null;
        type ??= TryGetString(record, "Type");
        return string.IsNullOrEmpty(type) ? "Other" : type;
    }

    private static string GetEquipmentCategoryLabel(
        string type,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations) =>
        newTranslations.TryGet(type, out var newLabel)
            ? newLabel
            : oldTranslations.TryGet(type, out var oldLabel)
                ? oldLabel
                : type;

    private static string EscapeHtml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string? RenderNameKey(string key, RecipeTranslations translations, RenderState state)
    {
        if (translations.TryGet(key, out var localized)) return LightEscape(localized);
        state.UntranslatedValues++;
        return null;
    }

    private static string? TryTranslate(string key, RecipeTranslations translations) =>
        translations.TryGet(key, out var localized) && !string.IsNullOrWhiteSpace(localized)
            ? LightEscape(localized)
            : null;

    private static string? RenderVariantName(JsonElement variant, RecipeTranslations translations, RenderState state)
    {
        if (TryGetString(variant, "NameKey") is { } nameKey && RenderNameKey(nameKey, translations, state) is { } named)
            return named;
        if (TryGetString(variant, "Index") is { } index && TryTranslate(index, translations) is { } localized)
            return localized;
        return TryGetString(variant, "Name") is { Length: > 0 } name ? LightEscape(name) : null;
    }

    /// <summary>
    /// The readable player-facing value, or null for an identifier, code or key that cannot be translated.
    /// </summary>
    private static string? RenderValue(
        JsonElement value,
        string fieldName,
        RecipeTranslations translations,
        RenderState state)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            if (fieldName == "RequiredClass" && string.IsNullOrWhiteSpace(text)) return "None";
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (fieldName == "NameKey") return RenderNameKey(text, translations, state);
            if (fieldName is "Type" or "Types" or "ETypes" or "classOnly" ||
                fieldName.EndsWith("Code", StringComparison.Ordinal))
                return TryTranslate(text, translations);
            if (fieldName is "Class" or "ClassSpecific")
                return PropertyKeyResolver.TryGetClassNameKey(text) is { } classKey
                    ? TryTranslate(classKey, translations)
                    : null;
            return LightEscape(text);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (IsKeyedLine(value)) return RenderPropertyLineText(value, translations, state);
            if (TryGet(value, "NameKey", out _) || TryGet(value, "Index", out _) || TryGet(value, "Name", out _))
                return RenderVariantName(value, translations, state);
            var children = value.EnumerateObject()
                .Select(property => RenderValue(property.Value, property.Name, translations, state) is { } child
                    ? FormatFieldName(property.Name) + ": " + child
                    : null)
                .OfType<string>()
                .ToArray();
            return children.Length > 0 ? string.Join("; ", children) : null;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().Select(item => RenderValue(item, fieldName, translations, state))
                .ToArray();
            return items.Length == 0 || items.Any(static item => item is null)
                ? null
                : string.Join(fieldName == "Runes" ? " → " : ", ", items);
        }

        if (value.ValueKind == JsonValueKind.Number && fieldName == "Type") return null;
        return LightEscape(RawJson(value));
    }

    private static bool IsKeyedLine(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        (TryGet(value, "key", out var key) && key.ValueKind == JsonValueKind.String ||
         TryGet(value, "nameKey", out var nameKey) && nameKey.ValueKind == JsonValueKind.String) &&
        TryGet(value, "args", out var args) && args.ValueKind == JsonValueKind.Array;

    private static bool IsKeyedLineArray(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 &&
        value.EnumerateArray().All(IsKeyedLine);

    private static bool IsPropertyChangePath(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
            PropertyArrayFields.Any(field =>
                segment == field || segment.StartsWith(field + "[", StringComparison.Ordinal))) ||
        path.Contains(".Lines[", StringComparison.Ordinal) || path.StartsWith("Lines[", StringComparison.Ordinal);

    private static bool IsKnownFieldPath(string path) =>
        LastPathName(path) is "Index" or "NameKey" or "Name" or "Code" or "Type" or "Types" or "ETypes" or
            "ItemLevel" or "Level" or "Rarity" or "ReqLevel" or "RequiredLevel" or "LevelReq" or
            "RequiredStrength" or "RequiredDexterity" or "GemSockets" or "Sockets" or "Durability" or
            "Defense" or "Damage" or "MinDamage" or "MaxDamage" or "AverageDamage" or "Block" or "Speed" or
            "Group" or "Class" or "ClassOnly" or "Runes" or "MagicLvl" or "Cost" or "Stackable" or
            "Throwable" or "TwoHanded" or "Equippable";

    private static string PropertyCollectionLabel(string path, RecipeTranslations translations)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var damageType =
            segments.FirstOrDefault(static segment => segment.StartsWith("DamageTypes[", StringComparison.Ordinal));
        if (damageType is not null)
        {
            var start = damageType.IndexOf('[') + 1;
            var end = damageType.IndexOf(']', start);
            var index = end > start && int.TryParse(damageType[start..end], NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsedIndex)
                ? Invariant(parsedIndex + 1)
                : end > start
                    ? damageType[start..end]
                    : "?";
            return $"Base damage type {LightEscape(index)} properties";
        }

        var variant = segments.FirstOrDefault(static segment => segment.StartsWith("Types[", StringComparison.Ordinal));
        if (variant is not null)
        {
            var start = variant.IndexOf('[') + 1;
            var end = variant.IndexOf(']', start);
            var name = end > start && translations.TryGet(variant[start..end], out var localized) &&
                       !string.IsNullOrWhiteSpace(localized)
                ? localized + " variant"
                : "Variant";
            return $"{name} properties";
        }

        var parts = segments
            .Select(part => part.Contains('[')
                ? part[..part.IndexOf('[')] + " variant"
                : part)
            .Where(static part => part.Length > 0);
        return string.Join(" ", parts.Select(FormatFieldName));
    }

    private static string FormatPathName(string path)
    {
        if (path.EndsWith("Equipment.NameKey", StringComparison.Ordinal)) return "Base Item";
        return string.Join(" ", path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(LastPathName).Select(FormatFieldName));
    }

    private static string LastPathName(string path)
    {
        var name = path[(path.LastIndexOf('.') + 1)..];
        var bracket = name.IndexOf('[');
        return bracket >= 0 ? name[..bracket] : name;
    }

    private static string FormatFieldName(string name) => name switch
    {
        "ReqLevel" or "LevelReq" or "RequiredLevel" => "Required Level",
        "ItemLevel" => "Item Level",
        "GemSockets" or "Sockets" => "Sockets",
        "RequiredStrength" => "Required Strength",
        "RequiredDexterity" => "Required Dexterity",
        "RequiredClass" => "Required Class",
        "MagicLvl" => "Magic Level",
        "AverageDamage" => "Average Damage",
        "DamageTypes" => "Damage Types",
        "ETypes" => "Excluded item types",
        "Types" => "Eligible types",
        "MaxLevel" => "Maximum Level",
        "ClassLevelReq" => "Required Class Level",
        "ClassSpecific" => "Class-specific",
        "NormCode" => "Normal version",
        "UberCode" => "Exceptional version",
        "UltraCode" => "Elite version",
        "SetBonuses" => "Set bonuses",
        "FullBonuses" => "Full set bonuses",
        "PartialBonuses" => "Partial set bonuses",
        _ => name
    };

    private static string TrimCommonSuffix(string oldText, string newText)
    {
        var oldWords = oldText.Split(' ', StringSplitOptions.None);
        var newWords = newText.Split(' ', StringSplitOptions.None);
        var shared = 0;
        while (shared < oldWords.Length && shared < newWords.Length &&
               string.Equals(oldWords[^(shared + 1)], newWords[^(shared + 1)], StringComparison.Ordinal))
            shared++;
        if (shared > 0 && shared < oldWords.Length) return string.Join(' ', oldWords[..^shared]);

        var sharedPrefix = 0;
        while (sharedPrefix < oldWords.Length && sharedPrefix < newWords.Length &&
               string.Equals(oldWords[sharedPrefix], newWords[sharedPrefix], StringComparison.Ordinal))
            sharedPrefix++;
        return sharedPrefix > 0 && sharedPrefix < oldWords.Length
            ? string.Join(' ', oldWords[sharedPrefix..])
            : oldText;
    }

    private static string CollapseNumericRangeParentheses(string value)
    {
        var result = new StringBuilder(value.Length);
        var position = 0;
        while (position < value.Length)
        {
            if (position + 1 < value.Length && value[position] == '(' && value[position + 1] == '(')
            {
                var end = value.IndexOf("))", position + 2, StringComparison.Ordinal);
                if (end > position + 2 && IsNumericRange(value[(position + 2)..end]))
                {
                    result.Append('(').Append(value, position + 2, end - position - 2).Append(')');
                    position = end + 2;
                    continue;
                }
            }

            result.Append(value[position++]);
        }

        return result.ToString();
    }

    private static bool IsNumericRange(string value)
    {
        for (var separator = 1; separator < value.Length - 1; separator++)
        {
            if (value[separator] != '-') continue;
            if (double.TryParse(value[..separator], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                double.TryParse(value[(separator + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return true;
        }

        return false;
    }

    internal static string CanonicalJson(JsonElement value)
    {
        var result = new StringBuilder();
        AppendCanonical(result, value);
        return result.ToString();
    }

    private static void AppendCanonical(StringBuilder result, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                result.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    if (!first) result.Append(',');
                    first = false;
                    result.Append(CanonicalString(property.Name)).Append(':');
                    AppendCanonical(result, property.Value);
                }

                result.Append('}');
                break;
            case JsonValueKind.Array:
                result.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) result.Append(',');
                    firstItem = false;
                    AppendCanonical(result, item);
                }

                result.Append(']');
                break;
            case JsonValueKind.Number:
                if (value.TryGetDecimal(out var decimalValue))
                    result.Append(decimalValue.ToString("G29", CultureInfo.InvariantCulture));
                else
                    result.Append(value.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.String:
                result.Append(CanonicalString(value.GetString() ?? ""));
                break;
            default:
                result.Append(RawJson(value));
                break;
        }
    }

    private static string CanonicalString(string value) => JsonSerializer.Serialize(value);

    internal static string LightEscape(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\' && index + 1 < value.Length &&
                value[index + 1] is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|')
            {
                result.Append(character).Append(value[++index]);
                continue;
            }

            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>' or '|') result.Append('\\');
            if (character is '\r' or '\n') result.Append(' ');
            else result.Append(character);
        }

        return result.ToString();
    }

    private static bool IsNestedPath(string path, string field) =>
        path.StartsWith(field + "[", StringComparison.Ordinal) || string.Equals(path, field, StringComparison.Ordinal);

    private static bool IsRuneSequencePath(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == "Runes" || segment.StartsWith("Runes[", StringComparison.Ordinal));

    private static void AppendHeading(StringBuilder markdown, int level, string text) =>
        markdown.Append(new string('#', level)).Append(' ').AppendLine(LightEscape(text));

    private static string? TryGetString(JsonElement element, string property) =>
        TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CategoryDefinition(string Id, string Title);

    private sealed record UniqueChange(
        string ChangeType,
        string Key,
        JsonElement Record,
        RecipeTranslations Translations,
        RecordEntry? Entry,
        ModifiedRecord? Modified);

    private sealed record SetChange(
        string ChangeType,
        string Key,
        JsonElement Record,
        RecordEntry? Entry,
        ModifiedRecord? Modified,
        RecipeTranslations Translations);

    private sealed record PropertyCollection(string Path, string Label, JsonElement Lines);

    internal sealed record PropertyDiff(IReadOnlyList<string> Changes);

    internal sealed class RenderState
    {
        public int UntranslatedValues { get; set; }

        public bool ConciseRecipeText { get; init; }
    }
}
