// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Import;

public static class EquipmentHelper
{
    private static string ResolveEquipmentName(EquipmentEntry baseEq, GameData data)
    {
        // Try magic name overrides first (e.g. aqv → "Magic Arrows").
        // Sourced from ExportConfig.MagicNameOverrides (Config/export-config.json).
        if (data.ExportConfig.MagicNameOverrides.TryGetValue(baseEq.NameStr, out var magicKey))
        {
            var magicName = data.Translations.GetValue(magicKey);
            if (!string.IsNullOrEmpty(magicName)) return magicName;
        }

        // For misc items, try translating the code itself (e.g. cs1 → "Latent Sunder Charm")
        if (baseEq.EquipmentType == EquipmentType.Jewelry)
        {
            var codeName = data.Translations.GetValue(baseEq.Code);
            if (!string.IsNullOrEmpty(codeName) && codeName != baseEq.Code)
                return codeName;
        }

        return data.Translations.GetValue(baseEq.NameStr);
    }

    public static EquipmentEntry? GetBaseEquipment(string code, GameData data)
    {
        if (data.Armors.TryGetValue(code, out var armor)) return armor;
        if (data.Weapons.TryGetValue(code, out var weapon)) return weapon;
        if (data.MiscItems.TryGetValue(code, out var misc))
        {
            return new EquipmentEntry
            {
                Code = misc.Code,
                NameStr = misc.NameStr,
                Type = misc.Type,
                Type2 = misc.Type2,
                Level = misc.Level,
                LevelReq = misc.LevelReq,
                EquipmentType = EquipmentType.Jewelry
            };
        }
        return null;
    }

    public static ExportEquipment? MapToExport(EquipmentEntry? baseEq, string itemName, List<CubePropertyExport> properties, GameData data, Config.ExportConfig config, int itemLevel)
    {
        if (baseEq == null) return null;

        string resolvedType = baseEq.Type ?? "";
        string typeIndex = resolvedType;
        if (!string.IsNullOrEmpty(resolvedType))
        {
            var code = resolvedType.Trim();
            if (data.ItemTypes.TryGetValue(code, out var it))
            {
                resolvedType = it.Name;
                typeIndex = it.Index;
            }
            else if (data.ItemTypesByName.TryGetValue(code, out var itByName))
            {
                resolvedType = itByName.Name;
                typeIndex = itByName.Index;
            }
        }

        var export = new ExportEquipment
        {
            Name = ResolveEquipmentName(baseEq, data),
            Code = baseEq.Code,
            // Propagate the source row's `namestr` so the keyed exporter can prefer
            // it as the translation key (falling back to Code when blank). Without
            // this, KeyedJsonExporter.MapEquipment always saw an empty NameStr on
            // armor/weapon rows and was forced to fall back to Code unconditionally
            // — i.e. a regression that lost the namestr → translation linkage.
            NameStr = baseEq.NameStr,
            EquipmentType = (int)baseEq.EquipmentType,
            BaseRequiredLevel = baseEq.LevelReq,
            ItemLevel = baseEq.Level,
            // Durability is finalized below (line ~105) where NoDurability=1
            // is honored. The previous "baseEq.Durability + 10" initializer
            // here was dead (immediately overwritten) and has been removed.
            RequiredStrength = baseEq.ReqStr?.ToString() ?? "0",
            RequiredDexterity = baseEq.ReqDex?.ToString() ?? "0",
            NormCode = baseEq.NormCode,
            UberCode = baseEq.UberCode,
            UltraCode = baseEq.UltraCode,
            AutoPrefix = baseEq.AutoPrefix?.ToString(),
            Type = new ItemTypeExport
            {
                Name = resolvedType,
                Index = typeIndex,
                Class = "" // Overwritten with typeEntry.Class below; "" is a safe default if the type lookup misses.
            }
        };

        // NoDurability=1 means item has no durability (bows, quivers) → set to 0
        export.Durability = (baseEq.NoDurability == 1) ? 0 : baseEq.Durability;

        if (baseEq.EquipmentType == EquipmentType.Armor)
        {
            // Only set Block for shields (shld type or equiv1=shld), matching old code
            if (data.ItemTypes.TryGetValue(baseEq.Type ?? "", out var blockTypeEntry) &&
                (string.Equals(blockTypeEntry.Equiv1, "shld", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(blockTypeEntry.Code, "shld", StringComparison.OrdinalIgnoreCase)))
            {
                export.Block = baseEq.Block ?? 0;
            }
            else
            {
                export.Block = null;
            }
            // Base ArmorString — DamageArmorCalculator will recalculate with ED/flat
            int minAc = baseEq.MinAC ?? 0;
            int maxAc = baseEq.MaxAC ?? 0;
            export.ArmorString = minAc == maxAc ? maxAc.ToString() : $"{minAc}-{maxAc}";
            // SmiteDamage and DamageString/DamageStringPrefix handled by DamageArmorCalculator
        }
        else if (baseEq.EquipmentType == EquipmentType.Weapon)
        {
            export.Speed = baseEq.Speed;
            export.StrBonus = baseEq.StrBonus;
            export.DexBonus = baseEq.DexBonus;
            export.DamageTypes = CalculateDamageTypes(baseEq, properties, itemLevel);
        }

        // Gem Sockets string and class-specific resolution
        if (data.ItemTypes.TryGetValue(baseEq.Type ?? "", out var typeEntry))
        {
            export.GemSockets = FormatGemSockets(typeEntry, baseEq.MaxSockets ?? 0);
            export.Type.Class = typeEntry.Class ?? "";

            // Resolve RequiredClass from Equiv2 (matches old doc-generator logic)
            var equiv2 = typeEntry.Equiv2;
            if (!string.IsNullOrEmpty(equiv2) && data.ItemTypes.TryGetValue(equiv2, out var equiv2Type))
            {
                var resolved = equiv2Type.Name?.Replace(" Item", "") ?? "";
                // Valid class-name set is configured via ExportConfig.ValidRequiredClassNames.
                if (data.ExportConfig.ValidRequiredClassNamesSet.Contains(resolved))
                {
                    export.RequiredClass = resolved;
                }
            }
        }

        // Phase 1B: emit base-equipment KeyedLine rows. DamageArmorCalculator may
        // append/replace defense and damage lines after this. Block-chance and
        // requirements come straight from the base item; durability + req-level
        // are appended once DamageArmorCalculator has finalized them.
        if (export.Block.HasValue && export.Block.Value > 0)
        {
            EquipmentLineBuilder.AppendBlock(export.Lines, export.Block.Value);
        }

        return export;
    }

    /// <summary>
    /// Phase 1B: emits the equipment requirement / durability KeyedLine rows after
    /// <see cref="DamageArmorCalculator"/> has applied any post-math adjustments.
    /// Call this from the importer once DamageArmorCalculator.Apply has run.
    /// </summary>
    public static void AppendFinalRequirementLines(ExportEquipment export, bool indestructible)
    {
        EquipmentLineBuilder.AppendDurability(export.Lines, export.Durability, indestructible);
        int? reqStr = int.TryParse(export.RequiredStrength, out var rs) ? rs : null;
        int? reqDex = int.TryParse(export.RequiredDexterity, out var rd) ? rd : null;
        EquipmentLineBuilder.AppendRequirements(export.Lines, reqStr, reqDex, export.RequiredLevel,
            string.IsNullOrEmpty(export.RequiredClass) ? null : export.RequiredClass);
    }

    /// <summary>
    /// Creates base damage type exports from raw equipment stats.
    /// No enhancements applied — DamageArmorCalculator handles ED/flat/elemental.
    /// </summary>
    private static List<DamageTypeExport> CalculateDamageTypes(EquipmentEntry baseEq, List<CubePropertyExport> properties, int itemLevel)
    {
        var damages = new List<DamageTypeExport>();

        bool has1H = baseEq.MinDamage.HasValue && baseEq.MinDamage.Value > 0;
        bool has2H = baseEq.TwoHandMinDamage.HasValue && baseEq.TwoHandMinDamage.Value > 0;
        bool hasMissile = baseEq.MissileMinDamage.HasValue && baseEq.MissileMinDamage.Value > 0;

        if (has1H && has2H)
        {
            damages.Add(CreateBaseDamageExport(0, baseEq.MinDamage!.Value, baseEq.MaxDamage!.Value));
            damages.Add(CreateBaseDamageExport(1, baseEq.TwoHandMinDamage!.Value, baseEq.TwoHandMaxDamage!.Value));
        }
        else if (has1H)
        {
            damages.Add(CreateBaseDamageExport(3, baseEq.MinDamage!.Value, baseEq.MaxDamage!.Value));
        }
        else if (has2H)
        {
            damages.Add(CreateBaseDamageExport(1, baseEq.TwoHandMinDamage!.Value, baseEq.TwoHandMaxDamage!.Value));
        }

        // Throw damage (Type 2) for missile weapons
        if (hasMissile)
        {
            damages.Add(CreateBaseDamageExport(2, baseEq.MissileMinDamage!.Value, baseEq.MissileMaxDamage!.Value));
        }

        return damages;
    }

    private static DamageTypeExport CreateBaseDamageExport(int type, int baseMin, int baseMax)
    {
        return new DamageTypeExport
        {
            Type = type,
            DamageString = baseMin == baseMax ? baseMax.ToString() : $"{baseMin} to {baseMax}",
            AverageDamage = (baseMin + baseMax) / 2.0
        };
    }

    private static string FormatGemSockets(ItemTypeEntry type, int maxInTxt)
    {
        if (maxInTxt == 0) return "";

        var parts = new List<string>();
        if (type.MaxSockets1.HasValue && type.MaxSockets1 > 0)
        {
            int val = Math.Min(type.MaxSockets1.Value, maxInTxt);
            if (type.MaxSocketsLevelThreshold1.HasValue)
                parts.Add($"(1-{type.MaxSocketsLevelThreshold1}): {val}");
            else
                parts.Add($"(1+): {val}");
        }
        if (type.MaxSockets2.HasValue && type.MaxSockets2 > 0)
        {
            int val = Math.Min(type.MaxSockets2.Value, maxInTxt);
            if (type.MaxSocketsLevelThreshold2.HasValue)
                parts.Add($"({type.MaxSocketsLevelThreshold1 + 1}-{type.MaxSocketsLevelThreshold2}): {val}");
            else
                parts.Add($"({type.MaxSocketsLevelThreshold1 + 1}+): {val}");
        }
        if (type.MaxSockets3.HasValue && type.MaxSockets3 > 0)
        {
            int val = Math.Min(type.MaxSockets3.Value, maxInTxt);
            parts.Add($"({type.MaxSocketsLevelThreshold2 + 1}+): {val}");
        }

        return string.Join(" - ", parts);
    }
}
