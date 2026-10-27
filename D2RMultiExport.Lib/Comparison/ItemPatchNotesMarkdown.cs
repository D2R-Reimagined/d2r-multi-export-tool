// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
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
        "Vanilla", "Enabled", "DamageArmorEnhanced", "EquipmentType", "Code"
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
        AppendSummary(markdown, report, oldTranslations, newTranslations, state);
        markdown.Append(sections);
        return markdown.ToString();
    }

    private static void AppendSummary(
        StringBuilder markdown,
        ComparisonReport report,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
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
        markdown.Append("English translations: old snapshot ").Append(EscapeMarkdown(oldTranslations.Status))
            .Append("; new snapshot ").Append(EscapeMarkdown(newTranslations.Status)).AppendLine(".");
        markdown.Append("Keyed names or properties needing review (fallback or unsupported template): ")
            .Append(Invariant(state.UntranslatedValues)).AppendLine(".");
        var diagnostics = CountIssues(report);
        markdown.Append("Comparison diagnostics: ").Append(Invariant(diagnostics.Errors)).Append(" errors, ")
            .Append(Invariant(diagnostics.Warnings)).AppendLine(" warnings.");
        markdown.AppendLine();
        markdown.AppendLine(
            "Only comparator-reported changes are listed. Unpaired property lines remain additions and removals; no buff or nerf classification is inferred.");
        markdown.AppendLine();
    }

    private static (int Errors, int Warnings) CountIssues(ComparisonReport report)
    {
        var errors = report.OldSnapshot.Errors.Count + report.NewSnapshot.Errors.Count;
        var warnings = report.OldSnapshot.Warnings.Count + report.NewSnapshot.Warnings.Count;
        foreach (var category in report.Categories)
        {
            errors += category.Errors.Count;
            warnings += category.Warnings.Count;
        }

        return (errors, warnings);
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
            markdown.Append("**Comparison unavailable: ")
                .Append(EscapeMarkdown(category?.Status ?? "missing from report"))
                .AppendLine(".**");
            markdown.AppendLine();
            markdown.AppendLine(
                "This category was not successfully compared; its absence does not mean that there were no item changes.");
            markdown.AppendLine();
            AppendIssues(markdown, category?.Errors ?? [], "Comparison errors");
            AppendIssues(markdown, category?.Warnings ?? [], "Comparison warnings");
            return;
        }

        AppendEntrySection(markdown, "Added", category.AddedRecords, definition, newTranslations, category, state);
        AppendEntrySection(markdown, "Removed", category.RemovedRecords, definition, oldTranslations, category, state);
        markdown.AppendLine("### Modified");
        markdown.AppendLine();
        if (category.ModifiedRecords.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
        }
        else if (definition.Id == "uniques")
        {
            AppendGroupedModifiedUniques(markdown, category.ModifiedRecords, definition, oldTranslations,
                newTranslations, state);
        }
        else
        {
            foreach (var record in category.ModifiedRecords)
                AppendModifiedRecord(markdown, record, definition, oldTranslations, newTranslations, state, 4);
        }

        if (category.Errors.Count > 0) AppendIssues(markdown, category.Errors, "Comparison errors");
        if (category.Warnings.Count > 0)
            AppendIssues(markdown, category.Warnings, "Comparison warnings and identity review");
    }

    private static void AppendEntrySection(
        StringBuilder markdown,
        string heading,
        IReadOnlyList<RecordEntry> records,
        CategoryDefinition definition,
        RecipeTranslations translations,
        CategoryComparison category,
        RenderState state)
    {
        markdown.Append("### ").AppendLine(heading);
        markdown.AppendLine();
        if (records.Count == 0)
        {
            markdown.AppendLine("*None.*");
            markdown.AppendLine();
            return;
        }

        if (definition.Id == "uniques")
        {
            foreach (var group in records.GroupBy(record => GetEquipmentCategory(record.Record, translations, state))
                         .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                markdown.Append("#### ").AppendLine(LightEscape(group.Key));
                markdown.AppendLine();
                foreach (var record in group.OrderBy(record => GetDisplayName(record.Record, record.Key, translations,
                                 state,
                                 countFallback: false),
                             StringComparer.Ordinal))
                    AppendEntry(markdown, record, definition, translations, category, state, 5);
            }

            return;
        }

        foreach (var record in records)
            AppendEntry(markdown, record, definition, translations, category, state, 4);
    }

    private static void AppendGroupedModifiedUniques(
        StringBuilder markdown,
        IReadOnlyList<ModifiedRecord> records,
        CategoryDefinition definition,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        foreach (var group in records.GroupBy(record => GetEquipmentCategory(record.New, newTranslations, state))
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            markdown.Append("#### ").AppendLine(LightEscape(group.Key));
            markdown.AppendLine();
            foreach (var record in group.OrderBy(record => GetDisplayName(record.New, record.Key, newTranslations,
                             state,
                             countFallback: false),
                         StringComparer.Ordinal))
                AppendModifiedRecord(markdown, record, definition, oldTranslations, newTranslations, state, 5);
        }
    }

    private static void AppendEntry(
        StringBuilder markdown,
        RecordEntry record,
        CategoryDefinition definition,
        RecipeTranslations translations,
        CategoryComparison category,
        RenderState state,
        int headingLevel)
    {
        AppendTraceComment(markdown, definition.Id, record.Key);
        AppendHeading(markdown, headingLevel, GetDisplayName(record.Record, record.Key, translations, state));
        markdown.AppendLine();
        if (definition.Id is not ("magicprefix" or "magicsuffix"))
            AppendVariantNames(markdown, record.VariantTypes);
        if (IsAmbiguousCandidate(record.Key, definition.Id, category))
            markdown.AppendLine(
                "- Identity review: ambiguous candidate; the comparison lists this as an addition/removal, not a confirmed item change.");
        AppendCompleteRecord(markdown, record.Record, definition.Id, translations, state);
        markdown.AppendLine();
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
        AppendTraceComment(markdown, definition.Id, record.Key);
        AppendHeading(markdown, headingLevel, GetDisplayName(record.New, record.Key, newTranslations, state));
        markdown.AppendLine();
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
            AppendVariantNames(markdown, record.VariantTypes);
        if (definition.Id is "magicprefix" or "magicsuffix" && record.MatchedBy is not null &&
            record.MatchedBy != "NameKey")
        {
            var matchMethod = record.MatchedBy == "PropertySignature"
                ? "property signature"
                : InlineCode(record.MatchedBy);
            markdown.Append("- Affix variant matched using ").Append(matchMethod).AppendLine(".");
        }

        AppendChangedMetadata(markdown, record, oldTranslations, newTranslations, state);
        AppendPropertyChanges(markdown, record.Old, record.New, oldTranslations, newTranslations, state);
        if (record.NestedChanges is { } nested)
            AppendNestedChanges(markdown, nested, definition.Id, oldTranslations, newTranslations, state);
        if (!record.Changes.Any(change => !IsPropertyChangePath(change.Path) &&
                                          (record.NestedChanges is null || !IsNestedPath(change.Path,
                                              record.NestedChanges.Field))) &&
            !HasPropertyChanges(record.Old, record.New) && record.NestedChanges is null)
            markdown.AppendLine("- The comparator reported a modification without a readable field value.");
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
            markdown.Append(new string(' ', depth * 2)).Append("- Record data: ")
                .AppendLine(RenderValue(value, "", translations, state));
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
                        var variantNameKey = TryGetString(variant, "NameKey");
                        var variantName = variantNameKey is not null
                            ? RenderNameKey(variantNameKey, translations, state)
                            : TryGetString(variant, "Name") ?? TryGetString(variant, "Type") ??
                            TryGetString(variant, "Index") ?? "Variant";
                        markdown.Append(new string(' ', depth * 2)).Append("- Variant: ")
                            .AppendLine(variantNameKey is null ? LightEscape(variantName) : variantName);
                        AppendMetadataTree(markdown, variant, translations, state, depth + 1, true);
                    }

                    continue;
                }

                AppendMetadataArray(markdown, name, property.Value, translations, state, depth);
                continue;
            }

            var label = FormatFieldName(name);
            markdown.Append(new string(' ', depth * 2)).Append("- ").Append(label).Append(": ")
                .AppendLine(RenderValue(property.Value, name, translations, state));
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
        if (string.IsNullOrWhiteSpace(nameKey)) nameKey = null;
        if (string.IsNullOrWhiteSpace(code)) code = null;
        if (nameKey is not null || code is not null)
        {
            markdown.Append(new string(' ', depth * 2)).Append("- Base Item: ");
            if (nameKey is not null)
                markdown.Append(RenderNameKey(nameKey, translations, state));
            else
                markdown.Append(LightEscape(code!));
            if (code is not null && nameKey is not null) markdown.Append(" [").Append(InlineCode(code)).Append(']');
            markdown.AppendLine();
        }

        foreach (var property in equipment.EnumerateObject())
        {
            if (property.Name is "NameKey" or "Code" or "NormCode" or "ExCode") continue;
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

            markdown.Append(new string(' ', depth * 2)).Append("- ").Append(FormatFieldName(property.Name)).Append(": ")
                .AppendLine(RenderValue(property.Value, property.Name, translations, state));
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
            var rendered = items.Select(item => RenderValue(item, name, translations, state));
            markdown.Append(new string(' ', depth * 2)).Append("- ").Append(label).Append(": ")
                .AppendLine(string.Join(", ", rendered));
            return;
        }

        markdown.Append(new string(' ', depth * 2)).Append("- ").Append(label).AppendLine(":");
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].ValueKind == JsonValueKind.Object)
            {
                var itemLabel = name == "DamageTypes" ? "Damage type" : label;
                markdown.Append(new string(' ', (depth + 1) * 2)).Append("- ").Append(itemLabel).Append(' ')
                    .Append(Invariant(index + 1)).AppendLine();
                AppendMetadataTree(markdown, items[index], translations, state, depth + 2, false);
            }
            else
                markdown.Append(new string(' ', (depth + 1) * 2)).Append("- ")
                    .AppendLine(RenderValue(items[index], name, translations, state));
        }
    }

    private static void AppendRuneSequence(
        StringBuilder markdown,
        JsonElement runes,
        RecipeTranslations translations,
        RenderState state,
        int depth)
    {
        markdown.Append(new string(' ', depth * 2)).Append("- Rune sequence: ")
            .AppendLine(RenderRuneSequence(runes, translations, state));
    }

    private static string RenderRuneSequence(
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
                : TranslateMaybe(code, translations);
        });
        return string.Join(" → ", values);
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

        markdown.AppendLine("- Properties:");
        foreach (var collection in collections)
        {
            var lines = collection.Lines.EnumerateArray().ToArray();
            var label = PropertyCollectionLabel(collection.Path);
            var prefix = label.Length == 0 ? "  " : $"  - {LightEscape(label)}:";
            if (label.Length > 0) markdown.AppendLine(prefix);
            if (lines.Length == 0)
            {
                markdown.Append(label.Length == 0 ? "  " : "    ").AppendLine("- None.");
                continue;
            }

            foreach (var line in lines)
                AppendPropertyLine(markdown, line, translations, state,
                    label.Length == 0 ? 1 : 2);
        }
    }

    private static void AppendPropertyChanges(
        StringBuilder markdown,
        JsonElement oldRecord,
        JsonElement newRecord,
        RecipeTranslations oldTranslations,
        RecipeTranslations newTranslations,
        RenderState state)
    {
        var oldCollections = GetPropertyCollections(oldRecord).ToDictionary(static collection => collection.Path,
            StringComparer.Ordinal);
        var newCollections = GetPropertyCollections(newRecord).ToDictionary(static collection => collection.Path,
            StringComparer.Ordinal);
        var paths = oldCollections.Keys.Concat(newCollections.Keys).Distinct(StringComparer.Ordinal).ToList();
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
                markdown.AppendLine("- Property changes:");
                emittedHeading = true;
            }

            if (paths.Count > 1 || path.Length > 0)
                markdown.Append("  - ").Append(LightEscape(PropertyCollectionLabel(path))).AppendLine(":");
            foreach (var change in diff.Changes)
                markdown.Append("    - ").AppendLine(change);
        }
    }

    private static PropertyDiff DiffProperties(
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
        var changedPairCount = 0;
        foreach (var (identity, oldIndexes) in oldByIdentity)
        {
            if (oldIndexes.Count != 1 || !newByIdentity.TryGetValue(identity, out var newIndexes) ||
                newIndexes.Count != 1)
                continue;
            var oldIndex = oldIndexes[0];
            var newIndex = newIndexes[0];
            oldMatched[oldIndex] = true;
            newMatched[newIndex] = true;
            changedPairCount++;
            var oldText = RenderPropertyLineText(oldLines[oldIndex], oldTranslations, state);
            var newText = RenderPropertyLineText(newLines[newIndex], newTranslations, state);
            changes.Add(newText == oldText
                ? $"Changed: {newText} (underlying data changed)"
                : $"Changed: {newText} (was {TrimCommonSuffix(oldText, newText)})");
        }

        var added = Enumerable.Range(0, newLines.Count).Where(index => !newMatched[index]).ToArray();
        var removed = Enumerable.Range(0, oldLines.Count).Where(index => !oldMatched[index]).ToArray();
        if (changedPairCount == 0 && added.Length == 0 && removed.Length == 0 && oldLines.Count == newLines.Count &&
            !oldLines.Select(CanonicalJson).SequenceEqual(newLines.Select(CanonicalJson), StringComparer.Ordinal))
            changes.Add("Property order changed.");

        foreach (var index in added)
            changes.Add($"Added: {RenderPropertyLineText(newLines[index], newTranslations, state)}");
        foreach (var index in removed)
            changes.Add($"Removed: {RenderPropertyLineText(oldLines[index], oldTranslations, state)}");
        return new PropertyDiff(changes);
    }

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
        var argsAreNumeric = !hasArgs || args.ValueKind == JsonValueKind.Array &&
            args.EnumerateArray().All(static argument => argument.ValueKind == JsonValueKind.Number);
        foreach (var property in line.EnumerateObject().Where(static property => property.Name != "args")
                     .OrderBy(static property => property.Name, StringComparer.Ordinal))
            stable.Append(property.Name).Append('=').Append(CanonicalJson(property.Value)).Append(';');
        if (hasArgs && !argsAreNumeric) stable.Append("args=").Append(CanonicalJson(args));
        identity = stable.ToString();
        return true;
    }

    private static string RenderPropertyLineText(JsonElement line, RecipeTranslations translations, RenderState state)
    {
        if (line.ValueKind != JsonValueKind.Object) return RenderValue(line, "", translations, state);
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
        else
        {
            state.UntranslatedValues++;
            var reason = hasTemplate ? "translation template unsupported; review" : "translation unavailable";
            rendered =
                $"{(key.Length == 0 ? "<empty key>" : key)} [{string.Join(", ", args.Select(RawJson))}] ({reason})";
        }

        rendered = CollapseNumericRangeParentheses(rendered);
        var annotations = new List<string>();
        var perLevelSuffix = "";
        foreach (var property in line.EnumerateObject())
        {
            if (property.Name is "key" or "nameKey" or "args" or "children") continue;
            if (property.Name == "classOnly" && property.Value.ValueKind == JsonValueKind.String)
            {
                var classKey = property.Value.GetString() ?? "";
                annotations.Add(translations.TryGet(classKey, out var classText)
                    ? classText
                    : $"classOnly={classKey}");
                if (!translations.TryGet(classKey, out _)) state.UntranslatedValues++;
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
                var qualifierText = translations.TryGet(qualifier, out var translatedQualifier)
                    ? translatedQualifier
                    : qualifier.Equals("Weapon", StringComparison.OrdinalIgnoreCase) ||
                      qualifier.Equals("Armor", StringComparison.OrdinalIgnoreCase) ||
                      qualifier.Equals("Shield", StringComparison.OrdinalIgnoreCase)
                        ? qualifier + " only"
                        : "Qualifier: " + qualifier;
                annotations.Add(qualifierText);
                continue;
            }

            if (property.Name == "perLevel" && property.Value.ValueKind == JsonValueKind.True)
            {
                perLevelSuffix = translations.TryGet("strPerCharacterLevelSuffix", out var suffix)
                    ? suffix
                    : "per character level";
                continue;
            }

            annotations.Add(
                $"{FormatFieldName(property.Name)}={RenderValue(property.Value, property.Name, translations, state)}");
        }

        if (annotations.Count > 0) rendered += " (" + string.Join("; ", annotations) + ")";
        if (perLevelSuffix.Length > 0)
            rendered += (char.IsWhiteSpace(perLevelSuffix[0]) ? "" : " ") + perLevelSuffix;
        return LightEscape(rendered);
    }

    private static void AppendPropertyLine(
        StringBuilder markdown,
        JsonElement line,
        RecipeTranslations translations,
        RenderState state,
        int depth)
    {
        markdown.Append(new string(' ', depth * 2)).Append("- ")
            .AppendLine(RenderPropertyLineText(line, translations, state));
        if (!TryGet(line, "children", out var children)) return;
        if (TryGet(line, "pickMode", out var pickMode))
            markdown.Append(new string(' ', (depth + 1) * 2)).Append("- Pick mode: ")
                .AppendLine(LightEscape(RawJson(pickMode)));
        if (children.ValueKind != JsonValueKind.Array)
        {
            markdown.Append(new string(' ', (depth + 1) * 2)).Append("- Unusual child data: ")
                .AppendLine(InlineCode(RawJson(children)));
            return;
        }

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
        var changes = record.Changes.Where(change => !IsPropertyChangePath(change.Path) &&
                                                     !IsRuneSequencePath(change.Path) &&
                                                     (record.NestedChanges is null ||
                                                      !IsNestedPath(change.Path, record.NestedChanges.Field)))
            .ToArray();
        if (changes.Length == 0 && !runeSequenceChanged) return;

        var known = changes.Where(change => IsKnownFieldPath(change.Path)).ToArray();
        var other = changes.Where(change => !IsKnownFieldPath(change.Path)).ToArray();
        if (known.Length > 0 || runeSequenceChanged)
        {
            markdown.AppendLine("- Changed details:");
            foreach (var change in known)
                AppendFieldChange(markdown, change, oldTranslations, newTranslations, state, "  ");
            if (runeSequenceChanged)
            {
                var oldRunes = TryGet(record.Old, "Runes", out var oldRuneValues)
                    ? RenderRuneSequence(oldRuneValues, oldTranslations, state)
                    : "<unavailable>";
                var newRunes = TryGet(record.New, "Runes", out var newRuneValues)
                    ? RenderRuneSequence(newRuneValues, newTranslations, state)
                    : "<unavailable>";
                markdown.Append("  - Rune sequence: ").Append(newRunes).Append(" (was ").Append(oldRunes)
                    .AppendLine(")");
            }
        }

        if (other.Length > 0)
        {
            markdown.AppendLine("- Other data changes (review):");
            foreach (var change in other)
                AppendFieldChange(markdown, change, oldTranslations, newTranslations, state, "  ");
        }
    }

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
        markdown.Append(indent).Append("- ");
        if (oldValue is null)
            markdown.Append("Added: ").Append(label).Append(": ").AppendLine(newValue ?? "<unavailable>");
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
            markdown.AppendLine("- Set-piece changes:");
            if (changes.Added.Count == 0 && changes.Removed.Count == 0 && changes.Modified.Count == 0)
            {
                markdown.AppendLine(
                    "  - The comparison reports nested set data changes without identifying an individual piece.");
                return;
            }

            AppendNestedSetPieceEntries(markdown, "Added", changes.Added, newTranslations, state);
            AppendNestedSetPieceEntries(markdown, "Removed", changes.Removed, oldTranslations, state);
            foreach (var nested in changes.Modified)
            {
                AppendTraceComment(markdown, category, nested.Key);
                AppendHeading(markdown, 5,
                    "Modified: " + GetDisplayName(nested.New, nested.Key, newTranslations, state));
                markdown.AppendLine();
                AppendModifiedRecordDetails(markdown, nested, new CategoryDefinition("set-piece", "Set piece"),
                    oldTranslations, newTranslations, state);
                markdown.AppendLine();
            }

            return;
        }

        markdown.AppendLine("- Possible automagic group changes:");
        markdown.AppendLine("  These are possible automagic associations; they do not imply a guaranteed affix roll.");
        if (changes.Added.Count == 0 && changes.Removed.Count == 0 && changes.Modified.Count == 0)
        {
            markdown.AppendLine(
                "  - The comparison reports nested automagic data changes without identifying an individual group.");
            return;
        }

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

    private static void AppendNestedSetPieceEntries(
        StringBuilder markdown,
        string status,
        IReadOnlyList<RecordEntry> records,
        RecipeTranslations translations,
        RenderState state)
    {
        foreach (var record in records)
        {
            AppendTraceComment(markdown, "sets", record.Key);
            AppendHeading(markdown, 5, status + ": " + GetDisplayName(record.Record, record.Key, translations, state));
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
            AppendTraceComment(markdown, category, record.Key);
            markdown.Append("    - ").AppendLine(GetDisplayName(record.Record, record.Key, translations, state));
            AppendVariantNames(markdown, record.VariantTypes, 3);
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
            markdown.Append("  - ").AppendLine(GetDisplayName(piece, key, translations, state));
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
            var name = nameKey is not null ? RenderNameKey(nameKey, translations, state)
                : code is not null ? TranslateMaybe(code, translations)
                : "Unnamed group";
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

    private static void AppendVariantNames(StringBuilder markdown, IReadOnlyList<string>? variants, int indent = 0)
    {
        if (variants is not { Count: > 0 }) return;
        markdown.Append(new string(' ', indent * 2)).Append("- Equipment variants: ")
            .AppendLine(string.Join(", ", variants.Select(InlineCode)));
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
        AppendAffixIdentityField(fields, record, "Group", "Group", translations, state);
        AppendAffixIdentityField(fields, record, "Types", "Eligible variants", translations, state);
        AppendAffixIdentityField(fields, record, "ETypes", "Eligible item types", translations, state);
        AppendAffixIdentityField(fields, record, "Class", "Class", translations, state);
        if (fields.Count > 0) markdown.Append("- Affix variant identity: ").AppendLine(string.Join("; ", fields));
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
        string rendered;
        if (value.ValueKind == JsonValueKind.Array)
        {
            rendered = string.Join(", ", value.EnumerateArray().Select(item =>
                item.ValueKind == JsonValueKind.String
                    ? InlineCode(item.GetString() ?? "")
                    : RenderValue(item, field, translations, state)));
        }
        else if (field == "Class" && value.ValueKind == JsonValueKind.String)
            rendered = InlineCode(value.GetString() ?? "");
        else
            rendered = RenderValue(value, field, translations, state);

        fields.Add(label + ": " + rendered);
    }

    private static string GetDisplayName(
        JsonElement record,
        string fallback,
        RecipeTranslations translations,
        RenderState state,
        bool countFallback = true)
    {
        var nameKey = TryGetString(record, "NameKey");
        if (nameKey is not null)
        {
            if (!countFallback)
                return translations.TryGet(nameKey, out var localizedName)
                    ? LightEscape(localizedName)
                    : LightEscape(nameKey);
            return RenderNameKey(nameKey, translations, state);
        }

        if (TryGetString(record, "Name") is { } name) return LightEscape(name);
        if (TryGetString(record, "Index") is { } index)
            return translations.TryGet(index, out var localized) ? LightEscape(localized) : LightEscape(index);
        if (TryGetString(record, "Code") is { } code) return LightEscape(code);
        return LightEscape(fallback);
    }

    private static string GetEquipmentCategory(JsonElement record, RecipeTranslations translations, RenderState state)
    {
        var type = TryGet(record, "Equipment", out var equipment) ? TryGetString(equipment, "Type") : null;
        type ??= TryGetString(record, "Type");
        return string.IsNullOrEmpty(type) ? "Other" : TranslateMaybe(type, translations);
    }

    private static string RenderNameKey(string key, RecipeTranslations translations, RenderState state)
    {
        if (translations.TryGet(key, out var localized)) return LightEscape(localized);
        state.UntranslatedValues++;
        return LightEscape(key) + " (translation unavailable)";
    }

    private static string TranslateMaybe(string value, RecipeTranslations translations)
    {
        if (translations.TryGet(value, out var localized)) return LightEscape(localized);
        return InlineCode(value);
    }

    private static string RenderValue(
        JsonElement value,
        string fieldName,
        RecipeTranslations translations,
        RenderState state)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            if (fieldName == "RequiredClass" && string.IsNullOrWhiteSpace(text)) return "None";
            if (text.Length == 0) return "<empty>";
            return fieldName is "NameKey"
                ? RenderNameKey(text, translations, state)
                : fieldName is "Type" or "classOnly"
                    ? TranslateMaybe(text, translations)
                    : LightEscape(text);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (TryGetString(value, "NameKey") is { } nameKey) return RenderNameKey(nameKey, translations, state);
            if (IsKeyedLine(value)) return RenderPropertyLineText(value, translations, state);
            var children = value.EnumerateObject().Select(property =>
                FormatFieldName(property.Name) + ": " +
                RenderValue(property.Value, property.Name, translations, state));
            return string.Join("; ", children);
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().Select(item => RenderValue(item, fieldName, translations, state));
            return string.Join(fieldName == "Runes" ? " → " : ", ", items);
        }

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

    private static bool HasPropertyChanges(JsonElement oldRecord, JsonElement newRecord)
    {
        var oldCollections = GetPropertyCollections(oldRecord).ToDictionary(static collection => collection.Path,
            StringComparer.Ordinal);
        var newCollections = GetPropertyCollections(newRecord).ToDictionary(static collection => collection.Path,
            StringComparer.Ordinal);
        return oldCollections.Keys.Concat(newCollections.Keys).Distinct(StringComparer.Ordinal).Any(path =>
        {
            oldCollections.TryGetValue(path, out var oldCollection);
            newCollections.TryGetValue(path, out var newCollection);
            return oldCollection is null || newCollection is null ||
                   !CanonicalJson(oldCollection.Lines)
                       .Equals(CanonicalJson(newCollection.Lines), StringComparison.Ordinal);
        });
    }

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

    private static string PropertyCollectionLabel(string path)
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
            var name = end > start ? variant[start..end] : "variant";
            return $"{LightEscape(name)} variant properties";
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
        "ETypes" => "Eligible item types",
        "Types" => "Eligible types",
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

    private static string CanonicalJson(JsonElement value)
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

    private static string LightEscape(string value)
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

    private static bool IsAmbiguousCandidate(string key, string category, CategoryComparison comparison) =>
        category is "magicprefix" or "magicsuffix" && key.Contains('#') &&
        comparison.Warnings.Any(static issue => issue.Code == "ambiguous-affix-identity");

    private static bool IsNestedPath(string path, string field) =>
        path.StartsWith(field + "[", StringComparison.Ordinal) || string.Equals(path, field, StringComparison.Ordinal);

    private static bool IsRuneSequencePath(string path) =>
        path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => segment == "Runes" || segment.StartsWith("Runes[", StringComparison.Ordinal));

    private static void AppendTraceComment(StringBuilder markdown, string category, string key) =>
        markdown.Append("<!-- changes.json: ").Append(category.Replace("--", "-", StringComparison.Ordinal))
            .Append(" / ").Append(key.Replace("--", "-", StringComparison.Ordinal)).AppendLine(" -->");

    private static void AppendHeading(StringBuilder markdown, int level, string text) =>
        markdown.Append(new string('#', level)).Append(' ').AppendLine(LightEscape(text));

    private static string? TryGetString(JsonElement element, string property) =>
        TryGet(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record CategoryDefinition(string Id, string Title);

    private sealed record PropertyCollection(string Path, string Label, JsonElement Lines);

    private sealed record PropertyDiff(IReadOnlyList<string> Changes);

    private sealed class RenderState
    {
        public int UntranslatedValues { get; set; }
    }
}
