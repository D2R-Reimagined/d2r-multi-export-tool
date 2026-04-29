// SPDX-License-Identifier: GPL-3.0-or-later
namespace D2RMultiExport.Lib.Models;

/// <summary>
/// Internal representation of an ItemStatCost entry used for property string generation.
/// Populated from D2RReimaginedTools parsing + stat-overrides.json.
/// </summary>
public sealed class StatEntry
{
    public required string Stat { get; init; }
    public int? DescriptionPriority { get; set; }
    public int? DescriptionFunction { get; set; }
    public int? DescriptionValue { get; set; }
    public string? DescStrPosKey { get; set; }
    public string? DescStrNegKey { get; set; }
    public string? DescStr2Key { get; set; }
    public string? DescriptionStringPositive { get; set; }
    public string? DescriptionStringNegative { get; set; }
    public string? DescriptionString2 { get; set; }
    public int? GroupDescription { get; set; }
    public int? GroupDescriptionFunction { get; set; }
    public int? GroupDescriptionValue { get; set; }
    public string? GroupDescriptionStringPositive { get; set; }
    public string? GroupDescriptionStringNegative { get; set; }
    public string? GroupDescriptionString2 { get; set; }
    public int? Op { get; set; }
    public int? OpParam { get; set; }
    public string? OpBase { get; set; }
    public string? OpStat1 { get; set; }
    public string? OpStat2 { get; set; }
    public string? OpStat3 { get; set; }
    public int? SaveBits { get; set; }
    public int? SaveAdd { get; set; }
    public int? SaveParam { get; set; }

    /// <summary>
    /// True if this stat was loaded from stat-overrides.json (synthetic stat).
    /// Used to prefer synthetic stats over Properties.txt stat1 lookups in resolution.
    /// </summary>
    public bool IsSynthetic { get; set; }

    public override string ToString() => Stat;
}

/// <summary>
/// Internal representation of a Properties.txt entry.
/// </summary>
public sealed class PropertyEntry
{
    public required string Code { get; init; }
    public string? Stat1 { get; init; }
    public int? Function1 { get; init; }
    public int? Set1 { get; init; }
    public int? Value1 { get; init; }

    public override string ToString() => Code;
}

/// <summary>
/// Internal representation of an ItemTypes.txt entry.
/// </summary>
public sealed class ItemTypeEntry
{
    public required string Code { get; init; }
    public required string Index { get; init; }
    public string? Class { get; init; }
    public string? Equiv1 { get; init; }
    public string? Equiv2 { get; init; }
    public string? BodyLoc1 { get; init; }
    public int? MaxSockets1 { get; init; }
    public int? MaxSocketsLevelThreshold1 { get; init; }
    public int? MaxSockets2 { get; init; }
    public int? MaxSocketsLevelThreshold2 { get; init; }
    public int? MaxSockets3 { get; init; }
    public int? Restricted { get; init; }

    /// <summary>
    /// Resolved display name (after normalization + translation).
    /// </summary>
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// Internal representation of a Skills.txt entry.
/// </summary>
public sealed class SkillEntry
{
    public required int Id { get; init; }
    public required string Skill { get; init; }
    public string? CharClass { get; init; }
    public string? SkillDesc { get; init; }
    public int RequiredLevel { get; init; }

    /// <summary>
    /// Resolved display name from translation (legacy English-text resolver path only).
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The translation-lookup key for this skill's display name. Resolved by following
    /// skills.txt <c>skilldesc</c> → skilldesc.txt → <c>str name</c> column. Falls back
    /// to the skills.txt <c>skill</c> code when no skilldesc row / str name exists.
    /// This is the value that should be emitted into <c>KeyedLine.Args</c> so that the
    /// website can <c>t(nameKey)</c> against <c>strings/&lt;lang&gt;.json</c>.
    /// </summary>
    public string NameKey { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// Internal representation of a SkillDesc.txt entry.
/// </summary>
public sealed class SkillDescEntry
{
    public required string SkillDesc { get; init; }
    public string? NameString { get; init; }

    public override string ToString() => SkillDesc;
}

/// <summary>
/// Internal representation of a CharStats.txt entry.
/// </summary>
public sealed class CharStatEntry
{
    public required string Class { get; init; }
    public string? StrAllSkills { get; init; }
    public string? StrSkillTab1 { get; init; }
    public string? StrSkillTab2 { get; init; }
    public string? StrSkillTab3 { get; init; }
    public string? StrClassOnly { get; init; }

    public override string ToString() => Class;
}

/// <summary>
/// A single sub-property within a property group.
/// </summary>
public sealed class PropertyGroupSubEntry
{
    public required string Property { get; init; }
    public string? Parameter { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }
    public int? Chance { get; init; }
}

/// <summary>
/// Internal representation of a propertygroups.txt entry.
/// </summary>
public sealed class PropertyGroupEntry
{
    public required string Code { get; init; }
    public int Id { get; init; }
    public int? PickMode { get; init; }
    public List<PropertyGroupSubEntry> SubProperties { get; init; } = [];
}

/// <summary>
/// Internal representation of a MonStats.txt entry.
/// </summary>
public sealed class MonStatEntry
{
    public required string Id { get; init; }
    public int HcIdx { get; init; }
    public string? NameStr { get; init; }
    public string? Name { get; set; }

    public override string ToString() => Name ?? Id;
}

/// <summary>
/// Internal representation of equipment from Armor.txt or Weapons.txt.
/// </summary>
public sealed class EquipmentEntry
{
    public required string Code { get; init; }
    public required string NameStr { get; init; }
    public required EquipmentType EquipmentType { get; init; }
    public string? Type { get; init; }
    public string? Type2 { get; init; }

    // Armor-specific
    public int? MinAC { get; init; }
    public int? MaxAC { get; init; }
    public int? Block { get; init; }

    // Weapon-specific
    public int? MinDamage { get; init; }
    public int? MaxDamage { get; init; }
    public int? TwoHandMinDamage { get; init; }
    public int? TwoHandMaxDamage { get; init; }
    public int? MissileMinDamage { get; init; }
    public int? MissileMaxDamage { get; init; }
    public int? StrBonus { get; init; }
    public int? DexBonus { get; init; }
    public string? TwoHandedWClass { get; init; }

    // Common
    public int? ReqStr { get; set; }
    public int? ReqDex { get; set; }
    public int? Durability { get; set; }
    public int? NoDurability { get; init; }
    public int? Speed { get; init; }
    public int? MaxSockets { get; init; }
    public string? GemSockets { get; init; }
    public int? Level { get; init; }
    public int? LevelReq { get; init; }
    public string? NormCode { get; init; }
    public string? UberCode { get; init; }
    public string? UltraCode { get; init; }
    public int? AutoPrefix { get; init; }

    /// <summary>
    /// Resolved display name from translation.
    /// </summary>
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

public enum EquipmentType
{
    Armor = 0,
    Weapon = 1,
    Jewelry = 2,
    Other = 3,
    Unknown = 4
}

/// <summary>
/// Internal representation of a Misc.txt entry.
/// </summary>
public sealed class MiscEntry
{
    public required string Code { get; init; }
    public required string NameStr { get; init; }
    public string? Type { get; init; }
    public string? Type2 { get; init; }
    public int Level { get; init; }
    public int LevelReq { get; init; }

    /// <summary>
    /// Resolved display name from translation.
    /// </summary>
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>
/// Internal representation of a Gems.txt entry.
/// </summary>
public sealed class GemEntry
{
    public required string Name { get; init; }
    public required string Code { get; init; }
    public string? Letter { get; init; }
    public List<ItemPropertyValue> WeaponProperties { get; init; } = [];
    public List<ItemPropertyValue> HelmProperties { get; init; } = [];
    public List<ItemPropertyValue> ShieldProperties { get; init; } = [];

    public override string ToString() => Name;
}

/// <summary>
/// Internal representation of a MagicPrefix/MagicSuffix/AutoMagic entry.
/// </summary>
public sealed class MagicAffixEntry
{
    public required string Name { get; init; }
    public int Level { get; init; }
    public int MaxLevel { get; init; }
    public int RequiredLevel { get; init; }
    public int Group { get; init; }
    public string ClassSpecific { get; init; } = "";
    public string Class { get; init; } = "";
    public int ClassLevelReq { get; init; }
    public List<string> Types { get; init; } = [];
    public List<string> ETypes { get; init; } = [];
    public List<ItemPropertyValue> Properties { get; init; } = [];

    public override string ToString() => Name;
}

/// <summary>
/// A single property value (code + parameter + min + max) used throughout the pipeline.
/// </summary>
public sealed class ItemPropertyValue
{
    public string? Code { get; init; }
    public string? Parameter { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }

    /// <summary>
    /// Reference to the resolved StatEntry (set during property string generation).
    /// </summary>
    public StatEntry? Stat { get; set; }

    /// <summary>
    /// The generated display string for this property.
    /// </summary>
    public string? DisplayString { get; set; }

    public override string ToString() => DisplayString ?? $"{Code}: {Min}-{Max}";
}
