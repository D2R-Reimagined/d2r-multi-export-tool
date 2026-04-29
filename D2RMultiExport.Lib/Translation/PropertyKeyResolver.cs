// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Translation;

/// <summary>
/// Resolves an <see cref="ExportProperty"/> into a list of <see cref="KeyedLine"/>
/// rows that comprise the keyed wire format. Each row carries:
///   - a <c>Key</c> that is either a real game translation key (e.g. <c>ModStr3a</c>,
///     <c>StrSklTabItem4</c>) or a synthetic key from <see cref="SyntheticStringRegistry.Keys"/>;
///   - an <c>Args</c> array holding finalized numeric values and/or string keys
///     (skill codes, class codes, monster name keys) for nested lookup on the website.
///
/// No string concatenation occurs here. All math (per-character-level scaling, ED%,
/// poison frame conversions, etc.) is performed before the args are placed into the row.
///
/// This resolver is invoked from <c>PropertyMapper.Map</c>; the resulting lines reach
/// the wire via <see cref="Exporters.KeyedJsonExporter"/>. There is no parallel
/// English-string rendering path — the keyed bundle plus per-language string maps
/// are the only output the consumer (the reimagined website) sees.
/// </summary>
public static class PropertyKeyResolver
{
    /// <summary>
    /// Maps a 3-letter CharStats class code to the D2R built-in
    /// <c>${Code}Only</c> translation key (e.g. <c>"nec"</c> → <c>"NecOnly"</c>),
    /// which renders the fully-localized parenthesized class-only phrase
    /// ("(Necromancer Only)", "(Nur Totenbeschwörer)", …). All seven base
    /// classes plus the mod's `war` (Warlock) class have a corresponding
    /// <c>${Code}Only</c> entry in CASC <c>item-modifiers.json</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ClassOnlyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ama", "AmaOnly" }, { "sor", "SorOnly" }, { "nec", "NecOnly" },
        { "pal", "PalOnly" }, { "bar", "BarOnly" }, { "dru", "DruOnly" },
        { "ass", "AssOnly" }, { "war", "WarOnly" }
    };

    /// <summary>
    /// Maps a 3-letter CharStats class code to the bare class-name translation
    /// key (e.g. <c>"nec"</c> → <c>"Necromancer"</c>). Used for the random-class
    /// random-skill template (<c>strSkillRandomClass</c>) where the class name
    /// alone (no parens) is required. The seven base classes use canonical
    /// CASC <c>ui.json</c> keys; the mod's <c>war</c> class uses the mod-side
    /// <c>Warlock</c> key.
    /// </summary>
    private static readonly Dictionary<string, string> ClassNameKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ama", "Amazon" },     { "sor", "Sorceress" }, { "nec", "Necromancer" },
        { "pal", "Paladin" },    { "bar", "Barbarian" }, { "dru", "Druid" },
        { "ass", "Assassin" },   { "war", "Warlock" }
    };

    private static string ResolveClassOnlyKey(string code) =>
        ClassOnlyKeys.TryGetValue(code, out var k) ? k : code;

    private static string ResolveClassNameKey(string code) =>
        ClassNameKeys.TryGetValue(code, out var k) ? k : code;

    /// <summary>
    /// Public lookup of the bare class-name translation key for a 3-letter
    /// CharStats class code (e.g. <c>"nec"</c> → <c>"Necromancer"</c>).
    /// Returns <c>null</c> when the code is empty or not recognized so callers
    /// can decide whether to omit a keyed field or fall back to the raw code.
    /// </summary>
    public static string? TryGetClassNameKey(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return ClassNameKeys.TryGetValue(code, out var k) ? k : null;
    }

    /// <summary>
    /// Public lookup of the parenthesized "(Class Only)" translation key for a
    /// 3-letter CharStats class code (e.g. <c>"nec"</c> → <c>"NecOnly"</c>).
    /// These are the same CASC <c>item-modifiers.json</c> keys used by uniques
    /// and sets to render their class-restriction footer ("(Necromancer Only)",
    /// "(Nur Totenbeschwörer)", …). Returns <c>null</c> when the code is empty
    /// or not recognized so callers can decide whether to omit the keyed field
    /// or fall back to the raw token.
    /// </summary>
    public static string? TryGetClassOnlyKey(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return ClassOnlyKeys.TryGetValue(code, out var k) ? k : null;
    }

    private static readonly Dictionary<int, string> SkillTabKeys = new()
    {
        { 0, "StrSklTabItem3"  }, { 1,  "StrSklTabItem2"  }, { 2,  "StrSklTabItem1"  },
        { 3, "StrSklTabItem15" }, { 4,  "StrSklTabItem14" }, { 5,  "StrSklTabItem13" },
        { 6, "StrSklTabItem8"  }, { 7,  "StrSklTabItem7"  }, { 8,  "StrSklTabItem9"  },
        { 9, "StrSklTabItem6"  }, { 10, "StrSklTabItem5"  }, { 11, "StrSklTabItem4"  },
        { 12, "StrSklTabItem11"}, { 13, "StrSklTabItem12" }, { 14, "StrSklTabItem10" },
        { 15, "StrSklTabItem16"}, { 16, "StrSklTabItem17" }, { 17, "StrSklTabItem18" },
        { 18, "StrSklTabItem19"}, { 19, "StrSklTabItem20" }, { 20, "StrSklTabItem21" },
        { 21, "StrSklTabItem24"}, { 22, "StrSklTabItem22" }, { 23, "StrSklTabItem23" },
    };


    /// <summary>
    /// Resolves the key-based wire representation for a property. Returns one or more
    /// <see cref="KeyedLine"/> rows (multiple rows are emitted for composite shapes that
    /// the legacy English path collapses to a single sentence — e.g. damage merges and
    /// elemental damage with duration).
    /// </summary>
    public static List<KeyedLine> Resolve(ExportProperty property, GameData data, int itemLevel)
    {
        var lines = new List<KeyedLine>();

        // 0. Hardcoded codes that don't resolve through Properties.txt → ItemStatCost.txt.
        switch (property.PropertyCode.ToLowerInvariant())
        {
            // dmg%, dmg-fire/-cold/-ltng/-pois/-mag, dmg-norm, ac, ac%, dmg-min, dmg-max:
            // all resolved through stat-overrides.json (synthetic ItemStatCost rows) →
            // ResolveStat → BuildLineForFunc, which renders the same in-game translation
            // keys (strModEnhancedDamage, strModFireDamageRange, strModColdDamageRange,
            // strModLightningDamageRange, strModPoisonDamageRange, strModMagicDamageRange,
            // strModMinDamageRange, ModStr1i, Modstr2v, ModStr1g, ModStr1f) via
            // descfunc 19 / 30 / 31. No hardcoded shortcut is needed; falling through
            // lets mod-overridden descstr keys win and keeps the configuration in one place.
            //
            // Unpaired cold-min/cold-max/fire-min/fire-max/ltng-min/ltng-max/pois-min/pois-max
            // remainders also fall through to their real ItemStatCost stat
            // (coldmindam, firemindam, lightmindam, poisonmindam, …) so the right
            // per-min/per-max descstrpos key is used rather than a hand-picked synthetic.
            case "ease":
            {
                // `ease` resolves through Properties.txt → `item_req_percent` ItemStatCost row,
                // which carries the real per-language descstrpos / descstrneg keys
                // (mod-overridable, e.g. ModStr3h "Requirements Reduced By %d%%" /
                // ModStr3h2 "Requirements Increased By %d%%"). Prefer those keys when
                // defined so mod authors' wording wins; fall back to the synthetic
                // strRequirementsReducedBy / strRequirementsIncreasedBy seeds otherwise.
                var min = property.Min ?? 0;
                var max = property.Max ?? min;
                var absArgs = ToRangeArgs(Math.Abs(min), Math.Abs(max));

                StatEntry? easeStat = null;
                if (data.Properties.TryGetValue("ease", out var easeProp)
                    && !string.IsNullOrEmpty(easeProp.Stat1))
                {
                    data.Stats.TryGetValue(easeProp.Stat1, out easeStat);
                }

                string? key = null;
                if (easeStat != null)
                {
                    key = min < 0
                        ? (easeStat.DescStrNegKey ?? easeStat.DescStrPosKey)
                        : (easeStat.DescStrPosKey ?? easeStat.DescStrNegKey);
                }
                if (string.IsNullOrEmpty(key))
                    key = min < 0
                        ? SyntheticStringRegistry.Keys.RequirementsReducedBy
                        : SyntheticStringRegistry.Keys.RequirementsIncreasedBy;

                lines.Add(new KeyedLine { Key = key, Args = absArgs });
                return lines;
            }
            case "reqlevel":
                lines.Add(new KeyedLine { Key = SyntheticStringRegistry.Keys.RequiredLevel, Args = [property.Min ?? 0] });
                return lines;
            case "fade":
                lines.Add(new KeyedLine { Key = "fadeDescription" });
                return lines;
            case "randclassskill":
                lines.Add(new KeyedLine { Key = SyntheticStringRegistry.Keys.SkillRandom, Args = [TryParseInt(property.Parameter) ?? 0] });
                return lines;
            case "charm-weight":
                lines.Add(new KeyedLine { Key = SyntheticStringRegistry.Keys.CharmWeight, Args = [property.Min ?? property.Max ?? 0] });
                return lines;
            case "2500":
                // strModMinDamageRange is a CASC item-modifier key (mod-overridable); leave as a literal.
                lines.Add(new KeyedLine { Key = "strModMinDamageRange", Args = ToRangeArgs(property.Min, property.Max) });
                return lines;
        }

        // Resolve to a stat for descfunc-driven rendering.
        StatEntry? stat = ResolveStat(property, data);
        if (stat == null)
        {
            // Last-resort: emit the raw code so the row is auditable rather than dropped.
            lines.Add(new KeyedLine { Key = property.PropertyCode, Args = ToRangeArgs(property.Min, property.Max) });
            return lines;
        }

        var line = ResolveStatLine(property, stat, data, itemLevel);
        if (line != null) lines.Add(line);
        return lines;
    }

    private static StatEntry? ResolveStat(ExportProperty property, GameData data)
    {
        if (data.Stats.TryGetValue(property.PropertyCode, out var direct) && direct.IsSynthetic)
            return direct;

        if (data.Properties.TryGetValue(property.PropertyCode, out var propEntry)
            && !string.IsNullOrEmpty(propEntry.Stat1)
            && data.Stats.TryGetValue(propEntry.Stat1, out var byStat1))
        {
            return byStat1;
        }

        var overrideKey = property.PropertyCode.ToLowerInvariant() switch
        {
            "indestruct" => "item_indesctructible",
            "str" => "strength",
            _ => null
        };
        if (overrideKey != null && data.Stats.TryGetValue(overrideKey, out var byOverride))
            return byOverride;

        if (data.Stats.TryGetValue(property.PropertyCode, out var byCode))
            return byCode;

        return null;
    }

    private static KeyedLine? ResolveStatLine(ExportProperty property, StatEntry stat, GameData data, int itemLevel)
    {
        // Pick the template key (positive vs negative) the same way the legacy resolver does.
        bool useNegative = property.Max.HasValue && property.Max.Value < 0
                           && !string.IsNullOrEmpty(stat.DescStrNegKey);
        var templateKey = useNegative ? stat.DescStrNegKey : stat.DescStrPosKey;

        // Synthetic stat-without-key rows (e.g. fade/extra-blood/indestructible) — we still
        // surface the stat name so the website can map it to a synthetic translation key.
        if (string.IsNullOrEmpty(templateKey))
        {
            switch (stat.Stat)
            {
                case "item_indesctructible":
                    return new KeyedLine { Key = SyntheticStringRegistry.Keys.Indestructible };
                case "item_cannotbefrozen":
                    return new KeyedLine { Key = SyntheticStringRegistry.Keys.CannotBeFrozen };
                case "item_levelreq":
                    return new KeyedLine { Key = SyntheticStringRegistry.Keys.RequiredLevel, Args = [property.Min ?? property.Max ?? 0] };
                case "fade":
                    return new KeyedLine { Key = "fadeDescription" };
                case "item_extrablood":
                    return new KeyedLine { Key = SyntheticStringRegistry.Keys.ItemExtraBlood };
                default:
                    // No descstrpos/descstrneg and no hand-mapped synthetic for this
                    // stat — drop the line entirely rather than leak a synthesized
                    // `str_<stat>` key that isn't declared in SyntheticStringRegistry
                    // and won't resolve on the website. Includes "*Dummy" ItemStatCost
                    // helpers (e.g. item_corruptedDummy), which are state-pair partner
                    // stats with no rendering of their own; the primary partner stat
                    // (e.g. item_corrupted → "Corrupted") is unaffected.
                    return null;
            }
        }

        int func = stat.DescriptionFunction ?? 1;
        return BuildLineForFunc(func, templateKey, stat, property, data, itemLevel);
    }

    private static KeyedLine BuildLineForFunc(int func, string templateKey, StatEntry stat, ExportProperty p, GameData data, int itemLevel)
    {
        switch (func)
        {
            case 1:  // "+%d Stat"
            case 2:  // "%d% Stat"
            case 3:  // "Stat" (value-less)
            case 4:  // "+%d% Stat"
            case 12: // "+%d Stat"
                return new KeyedLine { Key = templateKey, Args = ToRangeArgs(p.Min, p.Max) };

            case 5: // value scaled by 100/128
            {
                var v1 = p.Min.HasValue ? p.Min.Value * 100 / 128 : (int?)null;
                var v2 = p.Max.HasValue ? p.Max.Value * 100 / 128 : (int?)null;
                return new KeyedLine { Key = templateKey, Args = ToRangeArgs(v1, v2) };
            }

            case 6: // "+%d %s (%d/level)"
            case 7: // "%d%% %s (%d/level)"
            case 8: // "+%d %s (%d/level)"
            case 9: // "%s %d-%d (%d/level)"
            {
                // PerLevel composites — defer math to one synthetic shape carrying all parts.
                var growth1 = p.Min.HasValue ? p.Min.Value / 8d : 0d;
                var growth2 = p.Max.HasValue ? p.Max.Value / 8d : growth1;
                return new KeyedLine
                {
                    Key = SyntheticStringRegistry.Keys.PerLevelGrowthRange,
                    Args =
                    [
                        templateKey,
                        Round2(growth1),
                        Round2(growth2),
                        Math.Floor(growth1),
                        Math.Floor(growth2 * 99d)
                    ]
                };
            }

            case 11: // self-repair / replenish: rep-dur (item_repair) and rep-charges (item_replenish)
            {
                // The mod parameter is the number of seconds required to repair / replenish
                // 1 unit (matches the in-game wording "Repairs 1 Durability in N Seconds" —
                // see Basin wiki's Repairs Durability table where Self-Repair=33 means 33
                // seconds per durability point). Two templates exist and are paired:
                //   descstrpos (ModStre9t) — "Repairs %d durability per second" — used only
                //                            when the rate works out to exactly 1 unit per
                //                            1 second (param == 1).
                //   descstr2   (ModStre9u) — "Repairs %d durability in %d seconds" — used
                //                            for every other case, with args [1, param] so
                //                            the math reads "1 durability in <param> seconds".
                // (The previous descfunc-11 path divided by 100 and produced fractional values
                // like 0.07 that the website floored to "Repairs 0 durability per second";
                // Copperbite param=7 → "1 in 7 seconds", Gangrene Reaper param=15 → "1 in 15
                // seconds".)
                if (!int.TryParse(p.Parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                    return new KeyedLine { Key = templateKey, Args = [0] };

                if (seconds == 1)
                    return new KeyedLine { Key = templateKey, Args = [1] };

                // descfunc 11's two templates are conventionally paired ModStre9t / ModStre9u.
                // Vanilla itemstatcost.txt frequently leaves descstr2 blank for item_repair /
                // item_replenish (see Goblin Touch's `lgl` rep-charges row, which previously
                // emitted `ModStre9t [1, 10]` because the fallback was `templateKey`). Hardcode
                // ModStre9u so the right wording ("Repairs 1 durability in N seconds") is used
                // even when the data file omits the descstr2 reference.
                var altKey = !string.IsNullOrEmpty(stat.DescStr2Key) ? stat.DescStr2Key! : "ModStre9u";
                return new KeyedLine { Key = altKey, Args = [1, seconds] };
            }

            case 13: // "+%d to <Class> Skill Levels"
                return BuildClassSkill(templateKey, stat, p, data);

            case 14: // "+%d to <Skill Tab> (Class Only)"
                return BuildSkillTab(p, data);

            case 15: // skill on event
                return BuildSkillOnEvent(templateKey, stat, p, data, itemLevel);

            case 16: // "Level %d %s"
            {
                // Skill param may be a string skill name (most importers) or a
                // numeric row id (cube recipes read cubemain.txt raw). Resolve
                // through GameData so both shapes emit the same NameKey arg.
                var s16 = data.ResolveSkill(p.Parameter);
                if (s16 != null)
                {
                    return new KeyedLine
                    {
                        Key = templateKey,
                        Args = [p.Min ?? 0, s16.NameKey]
                    };
                }
                return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, p.Parameter ?? ""] };
            }

            case 19: // by-character-level / per-level template
            {
                // Only stats whose name explicitly indicates per-character-level scaling
                // (e.g. "*perlevel"/"*perlvl") should be wrapped in the PerCharacterLevel
                // synthetic. Synthetic stats (res-all, all-stats, fireskill, ...) reuse
                // descfunc 19 purely as a value-substitution shape — emit the plain
                // template with the value range, mirroring the legacy resolver's
                // FormatCase19 fallback branch.
                bool isPerLevel = stat.Stat != null
                                  && (stat.Stat.Contains("perlevel", System.StringComparison.OrdinalIgnoreCase)
                                      || stat.Stat.Contains("perlvl", System.StringComparison.OrdinalIgnoreCase));
                if (isPerLevel)
                {
                    // Per-character-level rows ride on the inner stat template (e.g. ModStr1f
                    // "%+d to Maximum Weapon Damage") with the growth value as its arg, and
                    // set PerLevel=true so the website appends the localized
                    // strPerCharacterLevelSuffix " (Based on Character Level)". The old
                    // strPerCharacterLevel wrapper "%s (Based on Character Level)" couldn't
                    // substitute the growth value into the inner template's %d / %+d slot,
                    // which left consumers rendering the bare template literal.
                    var div = stat.OpParam.HasValue ? Math.Pow(2, stat.OpParam.Value) : 1d;
                    double growth = double.TryParse(p.Parameter, NumberStyles.Any, CultureInfo.InvariantCulture, out var pp19)
                        ? pp19 / div
                        : (p.Min ?? 0) / div;
                    return new KeyedLine
                    {
                        Key = templateKey,
                        Args = [Round2(growth)],
                        PerLevel = true
                    };
                }
                return new KeyedLine { Key = templateKey, Args = ToRangeArgs(p.Min, p.Max) };
            }

            case 20: // negated %
                return new KeyedLine
                {
                    Key = templateKey,
                    Args = ToRangeArgs(p.Min.HasValue ? -p.Min : null, p.Max.HasValue ? -p.Max : null)
                };

            case 23: // monster reanimate-as
            {
                if (!string.IsNullOrEmpty(p.Parameter))
                {
                    string monsterRef = p.Parameter;
                    if (data.MonStats.TryGetValue(p.Parameter, out var m))
                        monsterRef = m.NameStr ?? p.Parameter;
                    else if (int.TryParse(p.Parameter, out var idx) && data.MonStatsByIndex.TryGetValue(idx, out var mIdx))
                        monsterRef = mIdx.NameStr ?? p.Parameter;
                    return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, monsterRef] };
                }
                return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, ""] };
            }

            case 24: // "Level %d %s (%d/%d Charges)"
            {
                var s24 = data.ResolveSkill(p.Parameter);
                if (s24 != null)
                {
                    // Min holds skill level, Max holds max-charges in the legacy semantics; current uses are flipped per resolver.
                    return new KeyedLine
                    {
                        Key = SyntheticStringRegistry.Keys.SkillCharges,
                        Args = [p.Min ?? 0, s24.NameKey, p.Max ?? 0, p.Max ?? 0]
                    };
                }
                return new KeyedLine
                {
                    Key = SyntheticStringRegistry.Keys.SkillCharges,
                    Args = [p.Min ?? 0, p.Parameter ?? "", p.Max ?? 0, p.Max ?? 0]
                };
            }

            case 27: // random-skill / +N to (Skill) (Class Only)
                return BuildRandomSkill(p, data);

            case 28: // "+%d to %s"
            {
                var s28 = data.ResolveSkill(p.Parameter);
                if (s28 != null)
                    return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, s28.NameKey] };
                return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, p.Parameter ?? ""] };
            }

            case 29: // sockets
            {
                int n = p.Min ?? p.Max ?? 0;
                if (n <= 0 && int.TryParse(p.Parameter, out var pn)) n = pn;
                return new KeyedLine { Key = SyntheticStringRegistry.Keys.SocketedCount, Args = [n] };
            }

            case 30: // poison damage with duration
            {
                int frames = TryParseInt(p.Parameter) ?? 0;
                int min = (int)Math.Ceiling((p.Min ?? 0) * frames / 256.0);
                int max = (int)Math.Ceiling((p.Max ?? 0) * frames / 256.0);
                double seconds = frames / 25.0;
                return new KeyedLine
                {
                    // strModPoisonDamageRange is a CASC item-modifier key (mod-overridable); leave as a literal.
                    Key = "strModPoisonDamageRange",
                    Args = [min, max, Round2(seconds)]
                };
            }

            case 31: // damage spread "%d-%d"
                return new KeyedLine { Key = templateKey, Args = ToRangeArgs(p.Min, p.Max) };

            default:
                return new KeyedLine { Key = templateKey, Args = ToRangeArgs(p.Min, p.Max) };
        }
    }

    private static KeyedLine BuildClassSkill(string templateKey, StatEntry stat, ExportProperty p, GameData data)
    {
        // Resolve class code from the parameter (numeric index or 3-letter code).
        string? classCode = null;
        if (!string.IsNullOrEmpty(p.Parameter))
        {
            if (int.TryParse(p.Parameter, out var classIdx) && ClassRangeConfig.CodeForClassIndex(classIdx) is { } byIdx)
                classCode = byIdx;
            else if (data.CharStatsByCode.ContainsKey(p.Parameter))
                classCode = p.Parameter;
        }

        if (classCode != null
            && data.CharStatsByCode.TryGetValue(classCode, out var charStat)
            && !string.IsNullOrEmpty(charStat.StrAllSkills))
        {
            // Emit the per-class CASC ModStr template named directly by
            // charstats.txt's StrAllSkills column (e.g. Barbarian → ModStr3e
            // "%+d to Barbarian Skill Levels", Druid → ModStre8a, Warlock →
            // ModStrge9). Each class's template already contains its
            // fully-localized class name, so there is no "(Class Only)"
            // suffix and no synthetic wrapper — just one key + one numeric
            // arg, fully data-driven from the mod's own charstats.txt.
            return new KeyedLine
            {
                Key = charStat.StrAllSkills!,
                Args = [p.Min ?? 0]
            };
        }

        // Unknown class code — fall back to the property's descstrpos
        // template (typically ModStr3a "+%d to Amazon Skill Levels"). This
        // keeps the line renderable while clearly signalling the unresolved
        // class; downstream consumers can decide how to surface it.
        return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0] };
    }

    private static KeyedLine BuildSkillTab(ExportProperty p, GameData data)
    {
        // Random skill tab (tab-rand): PropertyMapper normalizes Parameter to a
        // 3-letter CharStats class code (ama/sor/nec/pal/bar/dru/ass/war/...).
        // Emit a localizable "+%d Random Skill Tab" line and let the website
        // append the "(Class Only)" suffix via KeyedLine.ClassOnly — same
        // pattern as BuildClassSkill, so multi-language behaviour stays
        // consistent.
        if (!string.IsNullOrEmpty(p.Parameter)
            && !int.TryParse(p.Parameter, out _)
            && data.CharStatsByCode.ContainsKey(p.Parameter))
        {
            return new KeyedLine
            {
                Key = SyntheticStringRegistry.Keys.SkillTabRandomClassOnly,
                Args = [p.Min ?? 0],
                ClassOnly = ResolveClassOnlyKey(p.Parameter)
            };
        }

        if (!int.TryParse(p.Parameter, out var tabId) || !SkillTabKeys.TryGetValue(tabId, out var tabKey))
        {
            // Unknown tab id — fall back to a bare line carrying the raw
            // parameter so consumers can at least see the unresolved tab.
            return new KeyedLine
            {
                Key = SyntheticStringRegistry.Keys.SkillTabBonusClassOnly,
                Args = [p.Min ?? 0, p.Parameter ?? "", ""]
            };
        }

        // Find which class owns this tab.
        string? classCode = null;
        foreach (var cs in data.CharStats.Values)
        {
            if (cs.StrSkillTab1 == tabKey || cs.StrSkillTab2 == tabKey || cs.StrSkillTab3 == tabKey)
            {
                // Locate the 3-letter code by reverse lookup in CharStatsByCode.
                foreach (var kv in data.CharStatsByCode)
                {
                    if (ReferenceEquals(kv.Value, cs)) { classCode = kv.Key; break; }
                }
                break;
            }
        }

        // Emit the inner CASC tab template (e.g. StrSklTabItem4
        // "%+d to Defensive Auras") directly with the value, and let the
        // website append the localized "(Class Only)" suffix via
        // KeyedLine.ClassOnly. Wrapping it inside a synthetic with its own
        // "+%d to %s" prefix produced a doubled "to" once the inner
        // template's residual %+d was stripped on the consumer side.
        return new KeyedLine
        {
            Key = tabKey,
            Args = [p.Min ?? 0],
            ClassOnly = classCode != null ? ResolveClassOnlyKey(classCode) : null
        };
    }

    private static KeyedLine BuildSkillOnEvent(string templateKey, StatEntry stat, ExportProperty p, GameData data, int itemLevel)
    {
        var skill = data.ResolveSkill(p.Parameter);
        if (skill == null)
            return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, p.Max ?? 0, p.Parameter ?? ""] };

        int level = p.Max ?? 0;
        if (level == 0)
        {
            int req = skill.Id > 0 ? skill.Id : 1;
            int low = (int)Math.Min(Math.Ceiling((itemLevel - (req - 1)) / 3.9), 20);
            int high = (int)Math.Min(Math.Round((99 - (req - 1)) / 3.9), 20);
            level = low > 0 ? low : high;
        }

        return new KeyedLine { Key = templateKey, Args = [p.Min ?? 0, level, skill.NameKey] };
    }

    private static KeyedLine BuildRandomSkill(ExportProperty p, GameData data)
    {
        var skill = data.ResolveSkill(p.Parameter);
        if (skill != null)
        {
            // Skill carries CharClass — emit the (Class Only) composite when applicable.
            if (!string.IsNullOrEmpty(skill.CharClass) && data.CharStatsByCode.ContainsKey(skill.CharClass))
            {
                return new KeyedLine
                {
                    Key = SyntheticStringRegistry.Keys.SkillRandomFromSkillClass,
                    // Third arg = ${code}Only translation key, e.g. "NecOnly"
                    Args = [p.Min ?? 0, skill.NameKey, ResolveClassOnlyKey(skill.CharClass)]
                };
            }
            return new KeyedLine
            {
                Key = SyntheticStringRegistry.Keys.SkillRandomFromSkill,
                Args = [p.Min ?? 0, skill.NameKey]
            };
        }

        if (int.TryParse(p.Parameter, out var n))
        {
            // strSkillRandomClass is "+%d Random %s Skill" — %s is the bare
            // class-name key ("Amazon", "Necromancer", "Warlock", …) so the
            // website translates it cleanly. Falls back to the raw code only
            // when the skill-range bucket can't resolve.
            var classCode = ClassRangeConfig.CodeForSkillRange(p.Min, p.Max);
            return new KeyedLine
            {
                Key = SyntheticStringRegistry.Keys.SkillRandomClass,
                Args = [n, classCode != null ? ResolveClassNameKey(classCode) : ""]
            };
        }

        return new KeyedLine { Key = SyntheticStringRegistry.Keys.SkillRandom, Args = [p.Min ?? 0] };
    }



    private static object[] ToRangeArgs(int? min, int? max)
    {
        if (!min.HasValue && !max.HasValue) return [];
        if (!min.HasValue) return [max!.Value];
        if (!max.HasValue || min.Value == max.Value) return [min.Value];
        return [min.Value, max.Value];
    }

    private static object[] ToRangeArgs(double? min, double? max)
    {
        if (!min.HasValue && !max.HasValue) return [];
        if (!min.HasValue) return [max!.Value];
        if (!max.HasValue || min.Value == max.Value) return [min.Value];
        return [min.Value, max.Value];
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double Round2(double v) => Math.Round(v, 2);
}
