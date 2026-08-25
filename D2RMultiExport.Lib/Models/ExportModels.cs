// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Models;

/// <summary>
/// Export model for a unique item — this is what gets serialized to JSON / rendered in exports.
/// </summary>
public sealed class UniqueExport
{
    [JsonIgnore]
    public int FileIndex { get; set; }

    [JsonIgnore]
    public string? InventoryFile { get; set; }
    public string Type { get; set; } = "";
    public string Vanilla { get; set; } = "N";
    public string Name { get; set; } = "";
    public string Index { get; set; } = "";
    public bool Enabled { get; set; }
    public int Rarity { get; set; }
    public int ItemLevel { get; set; }
    public int RequiredLevel { get; set; }
    public string Code { get; set; } = "";
    public List<CubePropertyExport> Properties { get; set; } = [];
    public bool DamageArmorEnhanced { get; set; }
    public ExportEquipment? Equipment { get; set; }
}

/// <summary>
/// Export model for a runeword.
/// </summary>
public sealed class RunewordExport
{
    public List<RuneExport> Runes { get; set; } = [];
    public List<ItemTypeExport> Types { get; set; } = [];
    public string Vanilla { get; set; } = "N";
    public string Name { get; set; } = "";
    public string Index { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Rarity { get; set; } = 0;
    public int ItemLevel { get; set; }
    public int RequiredLevel { get; set; }
    public string Code { get; set; } = "";
    public List<CubePropertyExport> Properties { get; set; } = [];

    [JsonIgnore]
    public string? RuneName { get; set; }
}

public sealed class RuneExport
{
    public string Name { get; set; } = "";
    public int ItemLevel { get; set; }
    public int RequiredLevel { get; set; }
    public ItemTypeExport Type { get; set; } = new();
}

public sealed class ItemTypeExport
{
    public string Name { get; set; } = "";
    public string Index { get; set; } = "";
    public string Class { get; set; } = "";
}

/// <summary>
/// Export model for a complete set.
/// </summary>
public sealed class SetExport
{
    public string Name { get; set; } = "";
    public string Index { get; set; } = "";
    public int ItemLevel { get; set; } = 1;
    public string Vanilla { get; set; } = "N";
    public List<SetItemExport> SetItems { get; set; } = [];
    public List<CubePropertyExport> PartialBonuses { get; set; } = [];
    public List<CubePropertyExport> FullBonuses { get; set; } = [];
}

/// <summary>
/// Export model for a set item.
/// </summary>
public sealed class SetItemExport
{
    [JsonIgnore]
    public int FileIndex { get; set; }

    [JsonIgnore]
    public string? InventoryFile { get; set; }
    public string Type { get; set; } = "";
    public string Vanilla { get; set; } = "N";
    public string Name { get; set; } = "";
    public string Index { get; set; } = "";
    public string SetName { get; set; } = "";
    public int Rarity { get; set; }
    public int ItemLevel { get; set; }
    public int RequiredLevel { get; set; }
    public string Code { get; set; } = "";
    public List<CubePropertyExport> Properties { get; set; } = [];
    public List<List<CubePropertyExport>> SetBonuses { get; set; } = [];
    public bool DamageArmorEnhanced { get; set; }
    public ExportEquipment? Equipment { get; set; }
}

/// <summary>
/// Export model for a cube recipe.
/// </summary>
public sealed class CubeRecipeExport
{
    public int Index { get; set; }
    public string Description { get; set; } = "";
    public int? Op { get; set; }
    public int? Param { get; set; }
    public int? Value { get; set; }
    public string Class { get; set; } = "";

    /// <summary>
    /// Phase 1B keyed companion to <see cref="Class"/>. When the recipe's
    /// CubeMain.txt <c>class</c> column carries a 3-letter character class
    /// token (<c>ama</c>, <c>sor</c>, <c>nec</c>, …), this is populated with
    /// the corresponding parenthesized "(Class Only)" translation key
    /// (e.g. <c>"AmaOnly"</c>, <c>"SorOnly"</c>) — the same CASC
    /// <c>item-modifiers.json</c> keys that uniques and sets use for their
    /// class-restriction footer — wrapped in a <see cref="KeyedLine"/> so the
    /// website can localize it via <c>strings/{lang}.json</c>. Resolved by
    /// <see cref="Translation.PropertyKeyResolver.TryGetClassOnlyKey"/> and
    /// emitted as the <c>Class</c> field by
    /// <see cref="Exporters.KeyedJsonExporter"/>; <c>null</c> for non-class
    /// recipes.
    /// </summary>
    [JsonIgnore]
    public KeyedLine? ClassLine { get; set; }

    /// <summary>
    /// Plain English class-name string (e.g. <c>"Amazon"</c>, <c>"Sorceress"</c>)
    /// matching the shape used by <see cref="ExportEquipment.RequiredClass"/> on
    /// uniques, sets and runewords. The website's recipe renderer keys off this
    /// field to apply class-restriction styling, so it must be present alongside
    /// the keyed <see cref="ClassLine"/>. Empty string for non-class recipes
    /// (omitted from the keyed JSON via the pipeline's empty-string filter).
    /// </summary>
    public string RequiredClass { get; set; } = "";

    public int NumInputs { get; set; }
    public int ResolvedInputsCount { get; set; }
    public List<CubeIngredientExport> Inputs { get; set; } = [];
    public CubeOutputsExport Outputs { get; set; } = new();

    /// <summary>
    /// Recipe notes as translation keys (see <see cref="Translation.SyntheticStringRegistry.Keys"/>).
    /// Each entry is a <see cref="KeyedLine"/> whose <c>Key</c> is resolved by the
    /// website against <c>strings/{lang}.json</c>. Populated by
    /// <see cref="Import.CubeRecipeImporter"/> and serialized by
    /// <see cref="Exporters.KeyedJsonExporter"/>.
    /// </summary>
    [JsonIgnore]
    public List<KeyedLine> Notes { get; set; } = [];

    public bool Enabled { get; set; }
}
public sealed class CubeIngredientExport
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public List<string> Qualifiers { get; set; } = [];
    public string RawToken { get; set; } = "";

    /// <summary>
    /// Phase 1B keyed companion to <see cref="Name"/> — the synthetic / game key
    /// that the website resolves against <c>strings/{lang}.json</c>. Populated
    /// alongside <see cref="Name"/> by <see cref="Import.CubeRecipeImporter.ResolveName"/>;
    /// consumed by <see cref="Exporters.KeyedJsonExporter"/>.
    /// </summary>
    [JsonIgnore]
    public KeyedLine? NameLine { get; set; }

    /// <summary>
    /// Phase 1B keyed companion to <see cref="Qualifiers"/>. Populated from raw
    /// CubeMain.txt qualifier tokens via <see cref="Translation.CubeQualifierKeyMap"/>
    /// so the keyed bundle never carries hardcoded English friendly strings.
    /// </summary>
    [JsonIgnore]
    public List<KeyedLine> KeyedQualifiers { get; set; } = [];
}

public sealed class CubeOutputsExport
{
    public CubeOutputExport? A { get; set; }
    public CubeOutputExport? B { get; set; }
    public CubeOutputExport? C { get; set; }
}

public sealed class CubeOutputExport
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public List<string> Qualifiers { get; set; } = [];
    public int? OutputChance { get; set; }
    public List<CubePropertyExport> Properties { get; set; } = [];

    [JsonIgnore]
    public string MainToken { get; set; } = "";

    /// <summary>
    /// Phase 1B keyed companion to <see cref="Name"/>. See
    /// <see cref="CubeIngredientExport.NameLine"/> for rationale.
    /// </summary>
    [JsonIgnore]
    public KeyedLine? NameLine { get; set; }

    /// <summary>
    /// Phase 1B keyed companion to <see cref="Qualifiers"/>. See
    /// <see cref="CubeIngredientExport.KeyedQualifiers"/> for rationale.
    /// </summary>
    [JsonIgnore]
    public List<KeyedLine> KeyedQualifiers { get; set; } = [];
}

public sealed class MagicAffixExport
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int MaxLevel { get; set; }
    public int RequiredLevel { get; set; }
    public int Group { get; set; }
    public string ClassSpecific { get; set; } = "";
    public string Class { get; set; } = "";
    public int ClassLevelReq { get; set; }
    public List<CubePropertyExport> Properties { get; set; } = [];
    public List<string> Types { get; set; } = [];
    public List<string> ETypes { get; set; } = [];
    public string PType { get; set; } = "";
}

public sealed class CubePropertyExport
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ModChance { get; set; }
    public int Index { get; set; }

    /// <summary>
    /// For partial set bonuses (on <see cref="SetExport.PartialBonuses"/>) and for
    /// SetItem set bonuses with <c>add func == 2</c>, indicates how many items of
    /// the set must be equipped to activate this bonus (typically 2..5).
    /// Null for full set bonuses, regular item properties, and AddFunc==1
    /// "other set item" bonuses.
    /// </summary>
    [JsonPropertyName("number-of-items-required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NumberOfItemsRequired { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PickMode { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Chance { get; set; }

    [JsonPropertyName("group-properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<CubePropertyExport>>? GroupProperties { get; set; }

    [JsonIgnore]
    public bool IsPickModeEntry { get; set; }
    [JsonIgnore]
    public string? PropertyCode { get; set; }
    [JsonIgnore]
    public int Priority { get; set; }
    [JsonIgnore]
    public int? Min { get; set; }
    [JsonIgnore]
    public int? Max { get; set; }
    [JsonIgnore]
    public string? Parameter { get; set; }
    [JsonIgnore]
    public bool IsEase { get; set; }
    [JsonIgnore]
    public string Suffix { get; set; } = "";

    /// <summary>
    /// Key-based wire-format rows produced by <see cref="Translation.PropertyKeyResolver"/>.
    /// Consumed by <see cref="Exporters.KeyedJsonExporter"/>; this is the only
    /// rendering of a property that ever reaches the exported JSON.
    /// </summary>
    [JsonIgnore]
    public List<KeyedLine> Lines { get; set; } = [];
}

/// <summary>
/// Equipment details attached to an item export.
/// </summary>
public sealed class ExportEquipment
{
    public List<DamageTypeExport>? DamageTypes { get; set; }
    public string? DamageString { get; set; }
    public string? DamageStringPrefix { get; set; }
    public string? ArmorString { get; set; }
    public int? Block { get; set; }
    public int? Speed { get; set; }
    public int? StrBonus { get; set; }
    public int? DexBonus { get; set; }
    public string? GemSockets { get; set; }
    public int EquipmentType { get; set; }
    public string Name { get; set; } = "";
    /// <summary>
    /// Raw translation key from the source row's <c>namestr</c> field
    /// (e.g. "lbb" → "Buckler"). Falls back to <see cref="Code"/> when the
    /// row has no <c>namestr</c>. Used by the keyed exporter so the website
    /// resolves the active-language name itself instead of receiving an
    /// already-translated string.
    /// </summary>
    [JsonIgnore]
    public string? NameStr { get; set; }
    public int? BaseRequiredLevel { get; set; }
    public string? RequiredStrength { get; set; }
    public string? RequiredDexterity { get; set; }
    public int? Durability { get; set; }
    public int? ItemLevel { get; set; }
    public ItemTypeExport? Type { get; set; }
    public string? NormCode { get; set; }
    public string? UberCode { get; set; }
    public string? UltraCode { get; set; }
    public string? AutoPrefix { get; set; }
    public string RequiredClass { get; set; } = "";

    [JsonIgnore]
    public string Code { get; set; } = "";
    [JsonIgnore]
    public string? DefenseString { get; set; }
    [JsonIgnore]
    public int? MaxSockets { get; set; }
    [JsonIgnore]
    public int? RequiredLevel { get; set; }

    /// <summary>
    /// Key-based wire-format rows for the equipment block (defense, damage,
    /// durability, requirements, sockets, etc.). Populated by <c>EquipmentHelper</c>
    /// and <c>DamageArmorCalculator</c>, consumed exclusively by
    /// <see cref="Exporters.KeyedJsonExporter"/>. The companion English fields
    /// (e.g. <see cref="DamageString"/>) are kept only for in-process display
    /// fallbacks during import.
    /// </summary>
    [JsonIgnore]
    public List<KeyedLine> Lines { get; set; } = [];

    /// <summary>
    /// Creates a filtered copy suitable for embedding inside uniques/sets JSON.
    /// Strips fields that are only needed in the dedicated armors/weapons exports.
    /// Matches the old doc-generator's EquipmentFilterContractResolver.
    /// </summary>
    public ExportEquipment ToSlim()
    {
        // For jewelry (rings, amulets, charms), old export shows null for str/dex
        bool isJewelry = EquipmentType == 2;
        return new ExportEquipment
        {
            DamageTypes = DamageTypes,
            DamageString = DamageString,
            DamageStringPrefix = DamageStringPrefix,
            ArmorString = ArmorString,
            Block = Block,
            Speed = Speed,
            // StrBonus, DexBonus excluded
            // GemSockets excluded
            EquipmentType = EquipmentType,
            Name = Name,
            NameStr = NameStr,
            // BaseRequiredLevel excluded
            RequiredStrength = isJewelry ? null : RequiredStrength,
            RequiredDexterity = isJewelry ? null : RequiredDexterity,
            Durability = Durability,
            // ItemLevel excluded
            // Type excluded
            // NormCode, UberCode, UltraCode, AutoPrefix excluded
            RequiredClass = RequiredClass,
            // Internal fields
            Code = Code,
            DefenseString = DefenseString,
            MaxSockets = MaxSockets,
            RequiredLevel = RequiredLevel,
            Lines = [..Lines]
        };
    }
}

public sealed class DamageTypeExport
{
    public int Type { get; set; }
    public string DamageString { get; set; } = "";
    [JsonIgnore]
    public List<KeyedLine> Lines { get; set; } = [];
    public double AverageDamage { get; set; }
}

/// <summary>
/// A resolved property for export — the raw numeric/parametric data plus the
/// keyed wire-format rows produced by <see cref="Translation.PropertyKeyResolver"/>.
/// </summary>
public sealed class ExportProperty
{
    public string PropertyCode { get; set; } = "";
    public int? Min { get; set; }
    public int? Max { get; set; }
    public string? Parameter { get; set; }
    public int Priority { get; set; }

    /// <summary>
    /// Key-based wire-format rows for this property. One property may emit 1+
    /// lines (e.g. composite damage merges or charges-with-skill).
    /// </summary>
    [JsonIgnore]
    public List<KeyedLine> Lines { get; set; } = [];

    public override string ToString() => PropertyCode;
}

/// <summary>
/// Every description line D2 renders for one skill, split into the three boxes the game's
/// own tooltip uses, with each line's numbers already solved for every displayable skill
/// level. Keyed by <c>skills.txt</c> <c>*Id</c> in <see cref="GameData.SkillDescriptions"/>.
/// </summary>
public sealed class SkillDescriptionSet
{
    [JsonIgnore]
    public int SkillId { get; set; }

    /// <summary>
    /// Highest level the <see cref="SkillDescriptionLine.Values"/> tables cover — the
    /// skill's <c>maxlvl</c> plus the configured bonus-level headroom, so the website can
    /// preview what +skills gear does without the exporter shipping an unbounded table.
    /// </summary>
    public int MaxLevel { get; set; }

    /// <summary>Main tooltip stats (<c>descline1..6</c>): damage, duration, radius, …</summary>
    public List<SkillDescriptionLine> Stats { get; set; } = [];

    /// <summary>Supplementary lines (<c>dsc2line1..5</c>): mana cost, casting delay, …</summary>
    public List<SkillDescriptionLine> Details { get; set; } = [];

    /// <summary>The "Receives Bonuses From" list (<c>dsc3line1..7</c>).</summary>
    public List<SkillDescriptionLine> Synergies { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => Stats.Count == 0 && Details.Count == 0 && Synergies.Count == 0;
}

/// <summary>
/// One rendered description line. Shares <see cref="KeyedLine"/>'s camel-cased wire shape
/// and the same contract — <see cref="Key"/> is looked up in <c>strings/{lang}.json</c> and
/// the arguments are splatted positionally — but the numeric arguments arrive as one
/// per-level table rather than a single finalized value, because the same line renders
/// differently at every skill level.
///
/// Argument order on the wire is <see cref="Args"/> first (translation keys feeding the
/// template's leading <c>%s</c>), then one value per entry in <see cref="Values"/>, taken
/// at the level being displayed.
/// </summary>
public sealed class SkillDescriptionLine
{
    /// <summary>Translation key of the template, from the <c>desctextA</c> column.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    /// <summary>
    /// Plural template (<c>desctextB</c>) for the description functions that ship a
    /// singular/plural pair. The website picks this one whenever the rendered value is not
    /// exactly 1. Serialized only when the function supplies it.
    /// </summary>
    [JsonPropertyName("pluralKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluralKey { get; set; }

    /// <summary>
    /// Leading string arguments — translation keys, typically the name of the skill that
    /// grants a synergy bonus. Serialized only when present.
    /// </summary>
    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Args { get; set; }

    /// <summary>
    /// One table per numeric argument, indexed by <c>level - 1</c>. Tables stop at the last
    /// level whose value differs from its predecessor, so a line that does not scale ships a
    /// single entry — consumers clamp the index to the last element.
    /// Serialized only when the description function takes numeric arguments.
    /// </summary>
    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(LevelTableConverter))]
    public List<double[]>? Values { get; set; }
}
