// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Key-based wire-format exporter. Writes one JSON file per category to
/// <c>keyed/</c>, projecting each record down to identity fields + a flat
/// <c>lines: KeyedLine[]</c> array of post-math <c>{ key, args }</c> rows.
///
/// The website resolves every <see cref="KeyedLine.Key"/> against the active
/// language bundle (<c>strings/{lang}.json</c>) and substitutes
/// <see cref="KeyedLine.Args"/> positionally — no descfunc switch and no
/// numeric math runs on the client. This is the only exporter; the previously
/// shipped English-baked <c>JsonExporter</c> has been removed.
/// </summary>
public static class KeyedJsonExporter
{
    public static async Task ExportAsync(string exportDir, GameData data, bool prettyPrint = true)
    {
        var keyedDir = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(keyedDir);

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        if (data.Uniques.Count > 0)
        {
            var rows = data.Uniques.Select(MapUnique).ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "uniques.json"), rows, options);
        }

        if (data.Sets.Count > 0)
        {
            var rows = data.Sets.Select(MapSet).ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "sets.json"), rows, options);
        }

        if (data.Runewords.Count > 0)
        {
            var rows = data.Runewords.Select(MapRuneword).ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "runewords.json"), rows, options);
        }

        if (data.CubeRecipes.Count > 0)
        {
            var rows = data.CubeRecipes.Select(MapCubeRecipe).ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "cube-recipes.json"), rows, options);
        }

        // Aggregated automagic-group lookup, keyed by automagic.txt `group`.
        // Built once and shared by both armor and weapon emission so each base
        // item can attach the grouped affix bonuses associated with its
        // `AutoPrefix` (a Group id pointer into automagic.txt).
        var autoMagicGroupMap = BuildAutoMagicGroupMap(data);

        // Honor `ignoredUniqueItems` for base armor/weapon emission as well —
        // the list contains a mix of unique-item names and base item names
        // (quest items like "Pliers", "Gem Bag", "Keychain"), and any base whose
        // resolved English name matches an entry's substring should be dropped
        // alongside the corresponding unique. Substring + OrdinalIgnoreCase
        // matches the gating used by UniqueImporter.
        var ignoredItems = data.ExportConfig.IgnoredUniqueItemsSet;
        bool IsIgnoredBase(EquipmentEntry e) =>
            ignoredItems.Count > 0 && ignoredItems.Any(ig =>
                (e.Name ?? "").Contains(ig, StringComparison.OrdinalIgnoreCase));

        if (data.Armors.Count > 0)
        {
            var rows = data.Armors.Values
                .Where(a => !IsIgnoredBase(a))
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .Select(a => (Base: a, Export: BuildBaseEquipmentExport(a, data)))
                .Where(t => t.Export is not null)
                .Select(t => MapEquipmentWithAutoMagic(t.Export!, t.Base, autoMagicGroupMap, stripSocketTiers: false))
                .ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "armors.json"), rows, options);
        }

        if (data.Weapons.Count > 0)
        {
            var rows = data.Weapons.Values
                .Where(w => !IsIgnoredBase(w))
                .OrderBy(w => w.Name, StringComparer.Ordinal)
                .Select(w => (Base: w, Export: BuildBaseEquipmentExport(w, data)))
                .Where(t => t.Export is not null)
                .Select(t => MapEquipmentWithAutoMagic(t.Export!, t.Base, autoMagicGroupMap, stripSocketTiers: false))
                .ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "weapons.json"), rows, options);
        }

        if (data.MagicPrefixes.Count > 0)
        {
            // Preserve source row order — magic affixes are a row list, not a
            // keyed table; many rows share the same Name with different filters.
            var rows = data.MagicPrefixes
                .Select(p => MapMagicAffix(p, data, "Prefix"))
                .ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "magicprefix.json"), rows, options);
        }

        if (data.MagicSuffixes.Count > 0)
        {
            var rows = data.MagicSuffixes
                .Select(s => MapMagicAffix(s, data, "Suffix"))
                .ToList();
            await WriteJsonAsync(Path.Combine(keyedDir, "magicsuffix.json"), rows, options);
        }
    }


    // ---- DTOs -------------------------------------------------------------

    private sealed class KeyedUnique
    {
        public string Type { get; set; } = "";
        public string Vanilla { get; set; } = "N";
        // `Index` is the translation-lookup key (the UniqueItems.txt `index`
        // column). The previously-emitted `NameKey` field duplicated this with
        // the resolved English value and has been dropped — consumers should
        // resolve names via `strings/<lang>.json[index]`.
        public string Index { get; set; } = "";
        public bool Enabled { get; set; }
        public int Rarity { get; set; }
        public int ItemLevel { get; set; }
        public int RequiredLevel { get; set; }
        public string Code { get; set; } = "";
        public bool DamageArmorEnhanced { get; set; }
        public List<KeyedLine> Lines { get; set; } = [];
        public KeyedEquipment? Equipment { get; set; }
    }

    private sealed class KeyedSet
    {
        // `Index` is the translation-lookup key (Sets.txt `index`). `NameKey`
        // (resolved English) was redundant and has been removed.
        public string Index { get; set; } = "";
        public int ItemLevel { get; set; } = 1;
        public string Vanilla { get; set; } = "N";
        public List<KeyedSetItem> SetItems { get; set; } = [];
        public List<KeyedLine> PartialBonuses { get; set; } = [];
        public List<KeyedLine> FullBonuses { get; set; } = [];
    }

    private sealed class KeyedSetItem
    {
        public string Type { get; set; } = "";
        public string Vanilla { get; set; } = "N";
        // `Index` is the translation-lookup key (SetItems.txt `index`).
        public string Index { get; set; } = "";
        public string SetName { get; set; } = "";
        public int ItemLevel { get; set; }
        public int RequiredLevel { get; set; }
        public string Code { get; set; } = "";
        public bool DamageArmorEnhanced { get; set; }
        public List<KeyedLine> Lines { get; set; } = [];
        public List<List<KeyedLine>> SetBonuses { get; set; } = [];
        public KeyedEquipment? Equipment { get; set; }
    }

    private sealed class KeyedRuneword
    {
        // `Index` is the translation-lookup key (Runes.txt `Name`).
        public string Index { get; set; } = "";
        public string Vanilla { get; set; } = "N";
        public bool Enabled { get; set; } = true;
        public int Rarity { get; set; }
        public int ItemLevel { get; set; }
        public int RequiredLevel { get; set; }
        public string Code { get; set; } = "";
        public List<RuneRef> Runes { get; set; } = [];
        public List<ItemTypeExport> Types { get; set; } = [];
        public List<KeyedLine> Lines { get; set; } = [];
    }

    private sealed class RuneRef
    {
        public string NameKey { get; set; } = "";
        public int ItemLevel { get; set; }
        public int RequiredLevel { get; set; }
        public ItemTypeExport Type { get; set; } = new();
    }

    private sealed class KeyedCubeRecipe
    {
        public int Index { get; set; }
        public string Description { get; set; } = "";
        public int? Op { get; set; }
        public int? Param { get; set; }
        public int? Value { get; set; }
        public KeyedLine? Class { get; set; }
        public string RequiredClass { get; set; } = "";
        public int NumInputs { get; set; }
        public int ResolvedInputsCount { get; set; }
        public List<KeyedCubeIngredient> Inputs { get; set; } = [];
        public KeyedCubeOutputs Outputs { get; set; } = new();
        public List<KeyedLine> Notes { get; set; } = [];
        public bool Enabled { get; set; }
    }

    private sealed class KeyedCubeIngredient
    {
        public KeyedLine? Name { get; set; }
        public int Quantity { get; set; }
        public List<KeyedLine> Qualifiers { get; set; } = [];
        public string RawToken { get; set; } = "";
    }

    private sealed class KeyedCubeOutputs
    {
        public KeyedCubeOutput? A { get; set; }
        public KeyedCubeOutput? B { get; set; }
        public KeyedCubeOutput? C { get; set; }
    }

    private sealed class KeyedCubeOutput
    {
        public KeyedLine? Name { get; set; }
        public int Quantity { get; set; }
        public List<KeyedLine> Qualifiers { get; set; } = [];
        public int? OutputChance { get; set; }
        public List<KeyedLine> Lines { get; set; } = [];
    }

    private sealed class KeyedMagicAffix
    {
        public string NameKey { get; set; } = "";
        public int Level { get; set; }
        public int MaxLevel { get; set; }
        public int RequiredLevel { get; set; }
        public int Group { get; set; }
        public string ClassSpecific { get; set; } = "";
        public string Class { get; set; } = "";
        public int ClassLevelReq { get; set; }
        public List<string> Types { get; set; } = [];
        public List<string> ETypes { get; set; } = [];
        public string PType { get; set; } = "";
        public List<KeyedLine> Lines { get; set; } = [];
    }

    private sealed class KeyedEquipment
    {
        public int EquipmentType { get; set; }
        public string NameKey { get; set; } = "";
        public string? NormCode { get; set; }
        public string? UberCode { get; set; }
        public string? UltraCode { get; set; }
        public string? AutoPrefix { get; set; }
        public string RequiredClass { get; set; } = "";
        public ItemTypeExport? Type { get; set; }
        public List<KeyedDamageType>? DamageTypes { get; set; }
        /// <summary>
        /// Required character level to equip the base item (from the source
        /// row's <c>levelreq</c> column). Surfaced on base armor/weapon rows so
        /// consumers don't have to parse it out of the resolved <see cref="Lines"/>.
        /// </summary>
        public int? RequiredLevel { get; set; }
        /// <summary>
        /// Maximum socket count the base can roll. Pulled from
        /// <see cref="ExportEquipment.MaxSockets"/> (weapons.txt / armor.txt).
        /// </summary>
        public int? MaxSockets { get; set; }
        /// <summary>
        /// Pre-resolved gem socket level-tier display string (e.g. "(1-25): 1").
        /// Mirrors the legacy export's <c>GemSockets</c> field for base rows.
        /// </summary>
        public string? GemSockets { get; set; }
        public List<KeyedLine> Lines { get; set; } = [];
        /// <summary>
        /// Aggregated automagic-group bonuses attached to base armor/weapon
        /// rows whose <see cref="AutoPrefix"/> matches an automagic.txt
        /// <c>group</c> id. <c>null</c> on uniques/sets/runewords slim
        /// equipment and on bases with no matching group, so it is omitted
        /// from the JSON via <c>JsonIgnoreCondition.WhenWritingNull</c>.
        /// </summary>
        public List<KeyedAutoMagicGroup>? AutoMagicGroups { get; set; }
    }

    private sealed class KeyedAutoMagicGroup
    {
        /// <summary>Affix display name translation key (magicaffix.txt name column).</summary>
        public string NameKey { get; set; } = "";
        public int Level { get; set; }
        public int RequiredLevel { get; set; }
        public List<KeyedLine> Lines { get; set; } = [];
    }

    private sealed class KeyedDamageType
    {
        public int Type { get; set; }
        public double AverageDamage { get; set; }
        public List<KeyedLine> Lines { get; set; } = [];
    }


    // ---- Mapping ----------------------------------------------------------

    private static KeyedUnique MapUnique(UniqueExport u) => new()
    {
        Type = u.Type,
        Vanilla = u.Vanilla,
        Index = u.Index,
        Enabled = u.Enabled,
        Rarity = u.Rarity,
        ItemLevel = u.ItemLevel,
        RequiredLevel = u.RequiredLevel,
        Code = u.Code,
        DamageArmorEnhanced = u.DamageArmorEnhanced,
        Lines = FlattenPropertiesByPriority(u.Properties),
        Equipment = u.Equipment is null ? null : MapEquipment(u.Equipment)
    };

    private static KeyedSet MapSet(SetExport s) => new()
    {
        Index = s.Index,
        ItemLevel = s.ItemLevel,
        Vanilla = s.Vanilla,
        SetItems = s.SetItems.Select(MapSetItem).ToList(),
        PartialBonuses = FlattenProperties(s.PartialBonuses),
        FullBonuses = FlattenProperties(s.FullBonuses)
    };

    private static KeyedSetItem MapSetItem(SetItemExport i) => new()
    {
        Type = i.Type,
        Vanilla = i.Vanilla,
        Index = i.Index,
        SetName = i.SetName,
        ItemLevel = i.ItemLevel,
        RequiredLevel = i.RequiredLevel,
        Code = i.Code,
        DamageArmorEnhanced = i.DamageArmorEnhanced,
        Lines = FlattenProperties(i.Properties),
        SetBonuses = i.SetBonuses.Select(FlattenProperties).ToList(),
        Equipment = i.Equipment is null ? null : MapEquipment(i.Equipment)
    };

    private static KeyedRuneword MapRuneword(RunewordExport r) => new()
    {
        Index = r.Index,
        Vanilla = r.Vanilla,
        Enabled = r.Enabled,
        Rarity = r.Rarity,
        ItemLevel = r.ItemLevel,
        RequiredLevel = r.RequiredLevel,
        Code = r.Code,
        Runes = r.Runes.Select(rune => new RuneRef
        {
            NameKey = rune.Name,
            ItemLevel = rune.ItemLevel,
            RequiredLevel = rune.RequiredLevel,
            Type = rune.Type
        }).ToList(),
        Types = r.Types,
        Lines = FlattenPropertiesByPriority(r.Properties)
    };

    private static KeyedCubeRecipe MapCubeRecipe(CubeRecipeExport c) => new()
    {
        Index = c.Index,
        Description = c.Description,
        Op = c.Op,
        Param = c.Param,
        Value = c.Value,
        Class = c.ClassLine,
        RequiredClass = c.RequiredClass,
        NumInputs = c.NumInputs,
        ResolvedInputsCount = c.ResolvedInputsCount,
        Inputs = c.Inputs.Select(MapCubeIngredient).ToList(),
        Outputs = new KeyedCubeOutputs
        {
            A = MapCubeOutput(c.Outputs.A),
            B = MapCubeOutput(c.Outputs.B),
            C = MapCubeOutput(c.Outputs.C)
        },
        Notes = c.Notes,
        Enabled = c.Enabled
    };

    private static KeyedCubeIngredient MapCubeIngredient(CubeIngredientExport i) => new()
    {
        Name = i.NameLine,
        Quantity = i.Quantity,
        Qualifiers = i.KeyedQualifiers,
        RawToken = i.RawToken
    };

    private static KeyedCubeOutput? MapCubeOutput(CubeOutputExport? o) => o is null ? null : new KeyedCubeOutput
    {
        Name = o.NameLine,
        Quantity = o.Quantity,
        Qualifiers = o.KeyedQualifiers,
        OutputChance = o.OutputChance,
        Lines = FlattenProperties(o.Properties)
    };

    private static KeyedMagicAffix MapMagicAffix(MagicAffixEntry entry, GameData data, string pType)
    {
        var lines = new List<KeyedLine>();
        for (var i = 0; i < entry.Properties.Count; i++)
        {
            var prop = entry.Properties[i];
            if (!string.IsNullOrEmpty(prop.Code) && PropertyMapper.IsIgnored(prop.Code, data.ExportConfig)) continue;
            var resolved = PropertyMapper.Map(prop.Code ?? "", prop.Parameter, prop.Min, prop.Max, data, entry.Level);
            if (resolved.Lines.Count > 0)
            {
                lines.AddRange(resolved.Lines);
            }
        }

        return new KeyedMagicAffix
        {
            NameKey = entry.Name,
            Level = entry.Level,
            MaxLevel = entry.MaxLevel,
            RequiredLevel = entry.RequiredLevel,
            Group = entry.Group,
            ClassSpecific = entry.ClassSpecific ?? "",
            Class = entry.Class ?? "",
            ClassLevelReq = entry.ClassLevelReq,
            Types = entry.Types.Select(data.ResolveItemTypeIndex).ToList(),
            ETypes = entry.ETypes.Select(data.ResolveItemTypeIndex).ToList(),
            PType = pType,
            Lines = lines
        };
    }

    /// <summary>
    /// Synthetic socket-tier keys emitted on base armor/weapon rows by
    /// <see cref="Import.EquipmentHelper"/>'s socket-tier table. Uniques, sets
    /// and runewords own a fixed socket count via their <c>Sockets</c> field,
    /// so the per-character-level tier table from the underlying base type is
    /// meaningless noise on those rows and is filtered out when slim-Equipment
    /// is embedded.
    /// </summary>
    private static readonly HashSet<string> SocketTierKeys = new(StringComparer.Ordinal)
    {
        Translation.SyntheticStringRegistry.Keys.GemSocketsTier,
        Translation.SyntheticStringRegistry.Keys.GemSocketsOpen
    };

    /// <param name="stripSocketTiers">
    /// When true (default), socket-tier <see cref="KeyedLine"/>s are removed. Used for
    /// slim Equipment embedded in uniques/sets/runewords whose socket count is fixed
    /// and shipped via the <c>Sockets</c> field — the base type's level-tiered table
    /// would be misleading there. Base <c>armors.json</c>/<c>weapons.json</c> rows
    /// pass <c>false</c> so the tier rows survive.
    /// </param>
    private static KeyedEquipment MapEquipment(ExportEquipment e, bool stripSocketTiers = true) => new()
    {
        EquipmentType = e.EquipmentType,
        NameKey = !string.IsNullOrEmpty(e.NameStr) ? e.NameStr! : e.Code,
        NormCode = e.NormCode,
        UberCode = e.UberCode,
        UltraCode = e.UltraCode,
        AutoPrefix = e.AutoPrefix,
        RequiredClass = e.RequiredClass,
        Type = e.Type,
        DamageTypes = e.DamageTypes?.Select(d => new KeyedDamageType
        {
            Type = d.Type,
            AverageDamage = d.AverageDamage,
            Lines = d.Lines
        }).ToList(),
        RequiredLevel = e.RequiredLevel ?? e.BaseRequiredLevel,
        // Socket info (MaxSockets / GemSockets) is only meaningful on base
        // armor/weapon rows. Uniques, sets and runewords ship a fixed socket
        // count via their own `Sockets` field, so the per-base roll range and
        // the level-tier display string are misleading noise on the slim
        // Equipment embedded in those rows. We piggy-back on the existing
        // `stripSocketTiers` flag (true == slim embed) to gate them.
        MaxSockets = stripSocketTiers ? null : e.MaxSockets,
        GemSockets = stripSocketTiers ? null : e.GemSockets,
        Lines = stripSocketTiers
            ? e.Lines.Where(l => !SocketTierKeys.Contains(l.Key)).ToList()
            : e.Lines
    };

    /// <summary>
    /// Variant of <see cref="MapEquipment"/> used by base armor/weapon rows
    /// that need to surface the aggregated automagic-group bonuses associated
    /// with their <see cref="EquipmentEntry.AutoPrefix"/>. Uniques, sets,
    /// runewords keep using <see cref="MapEquipment"/> directly because their
    /// magical bonuses are baked-in and not derived from automagic.txt groups.
    /// </summary>
    private static KeyedEquipment MapEquipmentWithAutoMagic(
        ExportEquipment e,
        EquipmentEntry baseEq,
        Dictionary<int, List<KeyedAutoMagicGroup>> groupMap,
        bool stripSocketTiers = true)
    {
        var mapped = MapEquipment(e, stripSocketTiers);
        if (baseEq.AutoPrefix is { } group && groupMap.TryGetValue(group, out var groups) && groups.Count > 0)
        {
            mapped.AutoMagicGroups = groups;
        }
        return mapped;
    }

    /// <summary>
    /// Builds a <c>group → grouped lines</c> lookup from <see cref="GameData.AutoMagics"/>.
    /// Each group bucket aggregates by <c>(Name, Level, RequiredLevel)</c> the way the legacy
    /// doc-generator did, deduplicating identical resolved <see cref="KeyedLine"/>s, so the
    /// website can render every distinct affix that the base item can roll without re-running
    /// the property resolver.
    /// </summary>
    private static Dictionary<int, List<KeyedAutoMagicGroup>> BuildAutoMagicGroupMap(GameData data)
    {
        var map = new Dictionary<int, List<KeyedAutoMagicGroup>>();
        if (data.AutoMagics.Count == 0) return map;

        // Per-group, dedupe key-buckets keyed by Name+Level+RequiredLevel.
        var bucketIndex = new Dictionary<(int Group, string Name, int Level, int Req), KeyedAutoMagicGroup>();

        foreach (var am in data.AutoMagics)
        {
            if (am.Group <= 0 || data.ExportConfig.SkippedAutoMagicGroupsSet.Contains(am.Group)) continue;
            if (am.Properties.Count == 0) continue;

            // Resolve property lines once per affix row.
            var lines = new List<KeyedLine>();
            foreach (var prop in am.Properties)
            {
                if (string.IsNullOrEmpty(prop.Code) || PropertyMapper.IsIgnored(prop.Code, data.ExportConfig)) continue;
                var resolved = PropertyMapper.Map(prop.Code, prop.Parameter, prop.Min, prop.Max, data, am.Level);
                if (resolved.Lines.Count > 0) lines.AddRange(resolved.Lines);
            }
            if (lines.Count == 0) continue;

            var key = (am.Group, am.Name ?? "", am.Level, am.RequiredLevel);
            if (!bucketIndex.TryGetValue(key, out var bucket))
            {
                bucket = new KeyedAutoMagicGroup
                {
                    NameKey = am.Name ?? "",
                    Level = am.Level,
                    RequiredLevel = am.RequiredLevel,
                    Lines = []
                };
                bucketIndex[key] = bucket;
                if (!map.TryGetValue(am.Group, out var list))
                {
                    list = [];
                    map[am.Group] = list;
                }
                list.Add(bucket);
            }

            // Dedupe lines inside the bucket on (Key + arg signature).
            foreach (var line in lines)
            {
                if (!bucket.Lines.Any(existing => SameKeyedLine(existing, line)))
                {
                    bucket.Lines.Add(line);
                }
            }
        }

        // Order each group's buckets by RequiredLevel, then Level, then NameKey,
        // matching doc-generator's stable display order.
        foreach (var list in map.Values)
        {
            list.Sort((a, b) =>
            {
                var c = a.RequiredLevel.CompareTo(b.RequiredLevel);
                if (c != 0) return c;
                c = a.Level.CompareTo(b.Level);
                if (c != 0) return c;
                return string.Compare(a.NameKey, b.NameKey, StringComparison.Ordinal);
            });
        }

        return map;
    }

    private static bool SameKeyedLine(KeyedLine a, KeyedLine b)
    {
        if (!string.Equals(a.Key, b.Key, StringComparison.Ordinal)) return false;
        if (a.Args.Length != b.Args.Length) return false;
        for (int i = 0; i < a.Args.Length; i++)
        {
            var av = a.Args[i]?.ToString() ?? "";
            var bv = b.Args[i]?.ToString() ?? "";
            if (!string.Equals(av, bv, StringComparison.Ordinal)) return false;
        }
        return true;
    }


    /// <summary>
    /// Builds an <see cref="ExportEquipment"/> for a base (non-magical) item — i.e. the
    /// rows emitted into <c>armors.json</c> / <c>weapons.json</c>. We pass an empty
    /// property list through <see cref="DamageArmorCalculator"/> so the base damage /
    /// defense / durability / requirement <see cref="KeyedLine"/> rows are appended.
    /// </summary>
    private static ExportEquipment? BuildBaseEquipmentExport(EquipmentEntry baseEq, GameData data)
    {
        var export = EquipmentHelper.MapToExport(baseEq, baseEq.Name, [], data, data.ExportConfig, baseEq.Level ?? 0);
        if (export is null) return null;
        DamageArmorCalculator.Apply(export, baseEq, [], baseEq.LevelReq ?? 0, data);
        return export;
    }

    private static List<KeyedLine> FlattenProperties(List<CubePropertyExport> props)
    {
        var result = new List<KeyedLine>(capacity: props.Count);
        foreach (var p in props)
        {
            if (p.Lines.Count == 0) continue;
            result.AddRange(p.Lines);
        }
        return result;
    }

    /// <summary>
    /// Flatten variant that emits properties ordered by aggregate descPriority
    /// (descending) with the source-row index as a stable tiebreaker. Mirrors
    /// the legacy doc-generator behavior for unique- and runeword-item property
    /// lists, where higher <c>descpriority</c> stats display first and identical
    /// priorities preserve the source order. Keeps successive runs deterministic
    /// and prevents subtle layout drift in <c>uniques.json</c> /
    /// <c>runewords.json</c> when upstream property ordering changes.
    /// </summary>
    private static List<KeyedLine> FlattenPropertiesByPriority(List<CubePropertyExport> props)
    {
        var result = new List<KeyedLine>(capacity: props.Count);
        var ordered = props
            .Select((p, srcIndex) => (Prop: p, Src: srcIndex))
            .OrderByDescending(t => t.Prop.Priority)
            .ThenBy(t => t.Src)
            .Select(t => t.Prop);
        foreach (var p in ordered)
        {
            if (p.Lines.Count == 0) continue;
            result.AddRange(p.Lines);
        }
        return result;
    }

    private static async Task WriteJsonAsync<T>(string path, T data, JsonSerializerOptions options)
    {
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, data, options);
    }
}
