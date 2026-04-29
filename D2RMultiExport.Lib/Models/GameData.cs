// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Models;

/// <summary>
/// Central data context that replaces all static dictionaries from the old codebase.
/// Passed through the pipeline instead of relying on global mutable state.
/// </summary>
public sealed class GameData
{
    // Configuration (loaded from JSON files)
    public required ExportConfig ExportConfig { get; init; }
    public required StatOverrideConfig StatOverrideConfig { get; init; }
    public required TranslationService Translations { get; init; }

    // Lookup dictionaries (populated during LoadData phase)
    public Dictionary<string, StatEntry> Stats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PropertyEntry> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ItemTypeEntry> ItemTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ItemTypeEntry> ItemTypesByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SkillEntry> Skills { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SkillDescEntry> SkillDescs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, SkillEntry> SkillsById { get; } = [];
    public Dictionary<string, CharStatEntry> CharStats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CharStatEntry> CharStatsByCode { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MonStatEntry> MonStats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, MonStatEntry> MonStatsByIndex { get; } = new();
    public Dictionary<string, PropertyGroupEntry> PropertyGroups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, PropertyGroupEntry> PropertyGroupsById { get; } = new();
    public Dictionary<string, EquipmentEntry> Armors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, EquipmentEntry> Weapons { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MiscEntry> MiscItems { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, GemEntry> Gems { get; } = new(StringComparer.OrdinalIgnoreCase);
    // Magic affixes are inherently row-based and NOT uniquely keyed by Name —
    // many rows share the same display name with different IType/EType filters,
    // level brackets, groups, and class-specific variants. They must be stored
    // as ordered lists so duplicates are preserved through to the JSON export.
    public List<MagicAffixEntry> MagicPrefixes { get; } = [];
    public List<MagicAffixEntry> MagicSuffixes { get; } = [];
    public List<MagicAffixEntry> AutoMagics { get; } = [];
    public Dictionary<string, D2RReimaginedTools.Models.Sets> SetEntries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, D2RReimaginedTools.Models.SetItem> SetItemEntries { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Model collections (populated during ImportModel phase)
    public List<UniqueExport> Uniques { get; set; } = [];
    public List<RunewordExport> Runewords { get; set; } = [];
    public List<SetExport> Sets { get; set; } = [];
    public List<CubeRecipeExport> CubeRecipes { get; set; } = [];

    /// <summary>
    /// Resolves a 3-letter class code (e.g. "sor") or full class name to the display class name.
    /// </summary>
    public string ResolveClassName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var key = code.Trim();
        if (CharStatsByCode.TryGetValue(key, out var cs))
            return cs.Class;
        if (CharStats.TryGetValue(key, out cs))
            return cs.Class;
        return key;
    }

    /// <summary>
    /// Resolves a skill parameter (as it appears on a property's
    /// <c>Parameter</c> field) to a <see cref="SkillEntry"/>. Skill parameters
    /// can come through as either a string skill identifier (the
    /// <c>skills.txt</c> <c>Skill</c> column, e.g. <c>"Fire Bolt"</c>) or as a
    /// numeric row index / id (e.g. cube recipes pull <c>Mod{i}Param</c> raw
    /// from <c>cubemain.txt</c>, which often references the skill by its
    /// <c>*Id</c>). Numeric values take precedence and are looked up via
    /// <see cref="SkillsById"/>; otherwise we fall back to the name-keyed
    /// <see cref="Skills"/> dictionary. Returns <c>null</c> when the parameter
    /// is empty or cannot be resolved.
    /// </summary>
    public SkillEntry? ResolveSkill(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter)) return null;
        if (int.TryParse(parameter, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var skillId)
            && SkillsById.TryGetValue(skillId, out var byId))
        {
            return byId;
        }
        return Skills.TryGetValue(parameter, out var byName) ? byName : null;
    }

    /// <summary>
    /// Resolves an item type identifier (typically the itemtypes.txt Code, but
    /// historic callers may pass the same code redundantly) to the canonical
    /// Index value carried on <see cref="ItemTypeEntry"/>. Post-migration the
    /// Index column equals the Code, so this is effectively an identity lookup
    /// guarded against unknown ids — kept for callers (e.g. KeyedJsonExporter
    /// emitting MagicAffix Types/ETypes) that want defensive normalization.
    /// </summary>
    public string ResolveItemTypeIndex(string code)
    {
        return ItemTypes.TryGetValue(code, out var it) ? it.Index : code;
    }

    /// <summary>
    /// Resolves an item code to its translated name by checking armor, weapon, and misc lookups.
    /// </summary>
    public string? ResolveItemName(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;

        // Order: try Code as the translation key first (some misc rows use the code
        // itself as the localised string id, e.g. cs1, cjwl). Fall back to NameStr.
        // Use TryGetValue to avoid polluting the missing-keys report on the misses
        // we expect during the fallback chain.
        if (Armors.TryGetValue(code, out var armor))
            return ResolveBaseItemName(code, armor.NameStr);
        if (Weapons.TryGetValue(code, out var weapon))
            return ResolveBaseItemName(code, weapon.NameStr);
        if (MiscItems.TryGetValue(code, out var misc))
            return ResolveBaseItemName(code, misc.NameStr);

        return null;
    }

    private string ResolveBaseItemName(string code, string? nameStr)
    {
        if (Translations.TryGetValue(code, out var byCode) && !string.IsNullOrEmpty(byCode))
            return byCode;
        if (!string.IsNullOrEmpty(nameStr) && Translations.TryGetValue(nameStr, out var byName) && !string.IsNullOrEmpty(byName))
            return byName;
        // Last resort: record the missing-key probe against NameStr if available,
        // else against the code, so the gap shows up exactly once.
        return Translations.GetValue(!string.IsNullOrEmpty(nameStr) ? nameStr : code);
    }
}
