// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using Skills = D2RReimaginedTools.Models.Skills;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// The skill level a <see cref="SkillCalculator"/> evaluation is anchored to, plus the
/// lookup tables it needs to resolve cross-skill and missile references.
/// </summary>
internal sealed class SkillCalcContext : ICalcScope
{
    public required SkillEntry Skill { get; init; }
    public required GameData Data { get; init; }
    public required CalcLevels Levels { get; init; }

    private readonly HashSet<string> _expanding = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The rows already on the expansion stack, shared with every scope this one reaches
    /// into, so a pair of skills that quote each other cannot recurse forever. Kept apart
    /// from <see cref="ICalcScope.Expanding"/>, which is per-row: a minion skill reading
    /// its own <c>calc1</c> from inside its summoner's <c>calc1</c> is not a cycle.
    /// </summary>
    public HashSet<string> Visiting { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The scope of the skill whose description is actually being solved, when this scope is
    /// a nested one reached through a <c>skill('X'.clc5)</c> reference. Null on the scope
    /// that started the evaluation.
    ///
    /// An isolated preview allocates points to exactly one skill, so a reference has to know
    /// which one that is however deep it sits: Reimagined's synergy sums include the
    /// referencing skill itself (<c>Raven.calc5</c> and <c>Firestorm.calc5</c> each add up
    /// ten <c>blvl</c>s, their own among them), and a cell such as
    /// <c>min(clc6 + (skill('Raven'.clc5) - blvl), pa10)</c> is only correct because the
    /// sum's self term cancels the subtraction.
    /// </summary>
    public SkillCalcContext? Anchor { get; init; }

    /// <summary>The scope that owns the allocated skill levels — this one unless nested.</summary>
    private SkillCalcContext Described => Anchor ?? this;

    private int Level => Levels.Level;

    /// <summary>
    /// Why the last evaluation against this context failed, so a dropped description line
    /// can name the symbol or empty column that actually blocked it rather than the alias
    /// it was reached through.
    /// </summary>
    public CalcTrace Trace { get; init; } = new();

    CalcCodeTable ICalcScope.Codes => Data.SkillCalcCodes;
    HashSet<string> ICalcScope.Expanding => _expanding;

    /// <summary>
    /// The quantities <c>skillcalc.txt</c> names in prose rather than defining — the ones
    /// the game computes from a skill row's damage/mana/level columns. Everything the table
    /// *does* define (column aliases such as <c>clc0</c> → <c>calc10</c>, and the
    /// <c>lnXY</c>/<c>dmXY</c> progressions) is resolved from the table itself by
    /// <see cref="CalcExpression.TryResolveSymbol"/>.
    /// </summary>
    bool ICalcScope.TryResolveIntrinsic(string symbol, out long value)
    {
        value = 0;
        var row = Skill.SourceRow;
        if (row is null) return false;

        var level = Level;

        switch (symbol.ToLowerInvariant())
        {
            // Skill level. `lvl` is the effective level (hard points plus +skills), `blvl`
            // the hard points alone; reading the latter makes the line hard/soft sensitive
            // and switches it to a two-dimensional value grid on the wire.
            case "lvl":
            case "sklvl":
                value = level;
                return true;
            case "blvl":
                value = Levels.ReadBaseLevel();
                return true;

            // The caster's own character level. Independent of either skill level, so a
            // line reading it gets its own exported axis rather than being dropped.
            case "ulvl":
                value = Levels.ReadCharacterLevel();
                return true;

            // Attack rating bonus: ToHit plus LevToHit per level past the first.
            case "toht":
                value = Num(row.ToHit) + Num(row.LevToHit) * (level - 1);
                return true;

            // Mana cost. `usmc` is the 256ths form every description function divides by
            // 256; `mana` is the whole-point cost.
            case "usmc":
                value = ManaIn256ths(row, level);
                return true;
            case "mana":
                value = ManaIn256ths(row, level) / 256;
                return true;

            // Mana per second, for the channelled skills whose tooltip quotes an upkeep
            // rather than a cast cost. The game drains the 256ths cost once per frame at
            // 25 frames per second, so this is the same quantity `usmc` carries scaled to
            // a second and back out of 256ths.
            case "mps":
                value = ManaIn256ths(row, level) * 25 / 256;
                return true;

            // Elemental damage. `edmn`/`edmx` are the raw table values (Inner Sight's
            // defense penalty, Prayer's heal); `enma`/`exma` are the same value shifted
            // into displayable damage; the `…s`/`…ms` spellings are the 256ths form the
            // per-frame formulas multiply back up.
            case "edmn":
                value = ElementalMin(row, level);
                return true;
            case "edmx":
                value = ElementalMax(row, level);
                return true;
            case "edns":
                value = ElementalMin(row, level) * 256;
                return true;
            case "edxs":
                value = ElementalMax(row, level) * 256;
                return true;
            case "enma":
                value = Shift(ElementalMin(row, level), row) / 256;
                return true;
            case "exma":
                value = Shift(ElementalMax(row, level), row) / 256;
                return true;
            case "enms":
                value = Shift(ElementalMin(row, level), row);
                return true;
            case "exms":
                value = Shift(ElementalMax(row, level), row);
                return true;

            // Physical damage (summon and melee-skill columns).
            case "pnma":
                value = Shift(PhysicalMin(row, level), row) / 256;
                return true;
            case "pxma":
                value = Shift(PhysicalMax(row, level), row) / 256;
                return true;
            case "pnms":
                value = Shift(PhysicalMin(row, level), row);
                return true;
            case "pxms":
                value = Shift(PhysicalMax(row, level), row);
                return true;

            // Elemental length in frames (cold length, poison duration). `edma` is the
            // same length after Elemental Mastery, which contributes nothing in isolation.
            case "edln":
            case "edma":
                value = CalcExpression.LengthProgression(
                    Num(row.ELen), Num(row.ELevLen1), Num(row.ELevLen2), Num(row.ELevLen3), level);
                return true;

            // Mastery aggregates: how much a mastery the character has *elsewhere* adds to
            // this skill. A skill shown in isolation has no points in any other skill, so
            // these contribute nothing — the same convention `skill('X'.blvl)` already
            // uses, and the same number the game shows a character with no other points.
            case "mael": // elemental damage
            case "madm": // physical damage
            case "math": // attack rating
            case "macr": // critical hit
            case "mapi": // throwing pierce
            case "manc": // throwing no-consume
            case "mair": // item requirements
                value = 0;
                return true;
        }

        // The m1…/m2…/m3… families describe the skilldesc row's descmissile1..3.
        return TryResolveDescriptionMissile(symbol, out value);
    }

    /// <summary>
    /// Reads one source column off this skill's row. Param columns are plain numbers (an
    /// empty cell is a zero, as in game); every other column holds an expression evaluated
    /// in this same scope, and an empty cell means the description cannot be solved.
    /// </summary>
    bool ICalcScope.TryResolveColumn(string column, out long value)
    {
        value = 0;
        var row = Skill.SourceRow;
        if (row is null) return false;

        if (CalcExpression.TryParseIndexed(column, "Param", 1, 20, out var index))
        {
            value = Num(SkillCalcSource.Param(row, index));
            return true;
        }

        var expression = ExpressionColumn(row, column);
        return CalcExpression.TryEvaluateNested(column, expression, this, out value);
    }

    bool ICalcScope.TryResolveReference(string function, string target, List<string> fields, out long value)
    {
        value = 0;

        // `miss('firewall'.rang)` reaches into missiles.txt, evaluated at this skill's
        // level because a skill-spawned missile inherits the caster's skill level.
        if (function.Equals("miss", StringComparison.OrdinalIgnoreCase))
            return fields.Count == 1 && TryResolveMissile(target, fields[0], out value);

        // `cond('Difficulty', hell)` is the game's difficulty switch, used by the minion
        // damage lines; the rest of BBE's conditions interrogate a live unit.
        if (function.Equals("cond", StringComparison.OrdinalIgnoreCase))
            return CalcExpression.TryResolveCondition(Data, target, fields, Trace, out value);

        var described = Described;
        var isDescribed = string.Equals(target, described.Skill.Skill, StringComparison.OrdinalIgnoreCase);

        // `sklvl('Tainted Fire Ball'.lvl.edmn)` evaluates another skill's column at a level
        // the middle field names. Self-targeted (`sklvl('Summon Defiler'.lvl.lvl)`) is just
        // this skill; a level-independent Param column reads straight through; anything
        // else is a minion skill the describing skill grants.
        if (function.Equals("sklvl", StringComparison.OrdinalIgnoreCase))
        {
            if (fields.Count == 0) return false;
            if (isDescribed) return CalcExpression.TryResolveSymbol(fields[^1], described, out value);
            if (TryResolveOtherSkillParam(target, fields[^1], out value)) return true;
            return TryResolveMinionSkill(target, fields[^1], out value);
        }

        if (!function.Equals("skill", StringComparison.OrdinalIgnoreCase)
            && !function.Equals("stat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (function.Equals("skill", StringComparison.OrdinalIgnoreCase))
        {
            if (isDescribed)
                return fields.Count == 1 && CalcExpression.TryResolveSymbol(fields[0], described, out value);
            if (fields.Count == 1 && TryResolveUnallocatedSkill(target, fields[0], out value))
                return true;
        }

        return CalcExpression.TryResolveIsolatedSkill(
            Data, function, target, fields.Count == 1 ? fields[0] : null, out value);
    }

    /// <summary>
    /// Reads an expression-valued column off a skill the isolated preview has put no points
    /// into — <c>Cyclone Armor</c>'s <c>skill('Firestorm'.clc5)</c>, <c>Summon Goatman</c>'s
    /// <c>petmax = skill('Demonic Mastery'.clc1)</c>.
    ///
    /// Treating the whole term as zero would be wrong, because Reimagined's synergy columns
    /// count the referencing skill among their operands: <c>Raven.calc5</c> is a sum of ten
    /// <c>skill('…'.blvl)</c> terms, one of which is the very summon whose tooltip is being
    /// solved. Evaluating the column against the referenced row at <em>level 0</em> keeps the
    /// preview's single-skill allocation honest instead — every bare <c>lvl</c>/<c>blvl</c>
    /// inside it is zero, a nested reference back to <see cref="Described"/> reads the real
    /// level, and any other skill recurses into the same rule. The mod's own
    /// <c>(lvl&gt;=1)?(…):0</c> guards on <c>Shape Shifting</c>'s passive columns are written
    /// for exactly this substitution.
    ///
    /// Falls back to the flat zero of
    /// <see cref="CalcExpression.TryResolveIsolatedSkill"/> (by returning false) when the
    /// referenced column cannot be solved or when the reference cycles, so a column the mod
    /// leaves empty costs a term rather than the whole description line. The nested scope
    /// therefore gets its own <see cref="CalcTrace"/>: a failure that is swallowed must not
    /// become the reported root cause of some later, unrelated drop.
    /// </summary>
    public bool TryResolveUnallocatedSkill(string target, string field, out long value)
    {
        value = 0;
        if (!Data.Skills.TryGetValue(target, out var referenced) || referenced.SourceRow is null) return false;

        // A Param column does not scale, so it reads straight through at any allocation;
        // leave it to the shared isolated-skill policy.
        if (Data.SkillCalcCodes.FieldAliases.TryGetValue(field, out var column)
            && CalcExpression.TryParseIndexed(column, "Param", 1, 20, out _))
        {
            return false;
        }

        // Whatever this resolves to — the level-0 solve or the flat-zero fallback — is the
        // preview's zero allocation standing in for a rank the planner knows live.
        Described.Levels.ReadUnallocatedSkill();

        var guard = $"unallocated:{target}";
        if (!Visiting.Add(guard)) return false;
        try
        {
            var levels = new CalcLevels(0, 0, Levels.CharacterLevel);
            var scope = new SkillCalcContext
            {
                Skill = referenced,
                Data = Data,
                Levels = levels,
                Anchor = Described,
                Visiting = Visiting,
                Trace = new CalcTrace { ObserveIntrinsic = Trace.ObserveIntrinsic }
            };
            if (!CalcExpression.TryResolveSymbol(field, scope, out value))
            {
                value = 0;
                return false;
            }

            // `blvl` inside the nested row is a constant zero, so it must not make the
            // describing line hard/soft sensitive; `ulvl` is the caster's and still does.
            if (levels.UsedCharacterLevel) Levels.ReadCharacterLevel();
            return true;
        }
        finally
        {
            Visiting.Remove(guard);
        }
    }

    /// <summary>
    /// Resolves <c>sklvl('Tainted Fire Ball'.lvl.edmn)</c> — another skill's column, read
    /// at a level the reference itself names.
    ///
    /// The Data Guide spells the signature as
    /// <c>sklvl('Skill Name'.identifierToGetLevel.identifier)</c> and notes that the middle
    /// field "must be an identifier of the base BBE scope" — i.e. it is evaluated against
    /// the skill doing the asking, not the skill being read. Every one of the mod's ten
    /// uses passes <c>lvl</c>, so the referenced skill is solved at the describing skill's
    /// own levels, which is what the game does and not a convention this exporter invented.
    /// Sharing the caller's <see cref="CalcLevels"/> rather than flattening it to a single
    /// number also keeps the hard/soft split intact inside the referenced row.
    /// </summary>
    private bool TryResolveMinionSkill(string target, string field, out long value)
    {
        value = 0;
        if (!Data.Skills.TryGetValue(target, out var referenced))
            return Trace.Fail($"skills.txt has no row named '{target}'");
        if (referenced.SourceRow is null)
            return Trace.Fail($"'{target}' has no skills.txt row to read '{field}' from");

        var guard = $"skill:{target}";
        if (!Visiting.Add(guard)) return Trace.Fail($"sklvl('{target}'.…) refers back to itself");
        try
        {
            var scope = new SkillCalcContext
            {
                Skill = referenced,
                Data = Data,
                Levels = Levels,
                Anchor = Described,
                Visiting = Visiting,
                Trace = Trace
            };
            return CalcExpression.TryResolveSymbol(field, scope, out value);
        }
        finally
        {
            Visiting.Remove(guard);
        }
    }

    /// <summary>
    /// Reads a level-independent <c>Param</c> column off another skill. Used by the
    /// <c>sklvl</c> form, where the level the reference asks for is the other skill's and
    /// therefore unknowable — but irrelevant for a column that does not scale.
    /// </summary>
    private bool TryResolveOtherSkillParam(string target, string field, out long value)
    {
        value = 0;
        if (!Data.Skills.TryGetValue(target, out var referenced) || referenced.SourceRow is null) return false;
        if (!Data.SkillCalcCodes.FieldAliases.TryGetValue(field, out var column)) return false;
        if (!CalcExpression.TryParseIndexed(column, "Param", 1, 20, out var index)) return false;

        value = Num(SkillCalcSource.Param(referenced.SourceRow, index));
        return true;
    }

    /// <summary>
    /// Resolves the <c>m1en</c>/<c>m2ex</c>/<c>m3rn</c> family. Per <c>skillcalc.txt</c>
    /// these describe the *skilldesc* row's <c>descmissile1..3</c> — the missile the
    /// description wants to talk about — not the skill's own <c>SrvMissile</c>. The
    /// "…with Elemental Mastery" spellings fold onto the plain ones because a skill in
    /// isolation has no mastery points.
    /// </summary>
    private bool TryResolveDescriptionMissile(string symbol, out long value)
    {
        value = 0;
        var lower = symbol.ToLowerInvariant();
        if (lower.Length != 4 || lower[0] != 'm') return false;

        // `me3o`/`me3y` are the table's own spelling of missile 3's 256ths codes.
        var (slot, code) = lower is "me3o" or "me3y"
            ? (3, lower[3] == 'o' ? "eo" : "ey")
            : (lower[1] - '0', lower[2..]);
        if (slot is < 1 or > 3) return false;

        var missileSymbol = code switch
        {
            "en" or "nm" => "edmn",
            "ex" or "xm" => "edmx",
            "el" => "edln",
            "eo" => "edns",
            "ey" => "edxs",
            "rn" => "rang",
            _ => null
        };
        if (missileSymbol is null) return false;

        if (string.IsNullOrEmpty(Skill.SkillDesc)
            || !Data.SkillDescs.TryGetValue(Skill.SkillDesc, out var desc)) return false;

        var missile = slot switch
        {
            1 => desc.DescriptionMissile1,
            2 => desc.DescriptionMissile2,
            _ => desc.DescriptionMissile3
        };
        return !string.IsNullOrWhiteSpace(missile) && TryResolveMissile(missile, missileSymbol, out value);
    }

    /// <summary>
    /// Resolves one symbol against this skill on behalf of a missile it spawned, so a
    /// <c>missiles.txt</c> cell that names its own caster — <c>moltenboulderfirepath</c>'s
    /// <c>skill('Molten Boulder'.blvl)</c> — reads the real level instead of falling
    /// through the isolated-preview policy to zero.
    /// </summary>
    public bool TryResolveOwnSymbol(string field, out long value)
        => CalcExpression.TryResolveSymbol(field, this, out value);

    private bool TryResolveMissile(string missileName, string field, out long value)
    {
        value = 0;
        if (!Data.Missiles.TryGetValue(missileName, out var row))
            return Trace.Fail($"missiles.txt has no row named '{missileName}'");

        var scope = new MissileCalcContext { Row = row, Data = Data, Levels = Levels, Owner = this, Trace = Trace };
        return CalcExpression.TryResolveSymbol(field, scope, out value);
    }

    private static long ManaIn256ths(Skills row, int level)
        => Math.Max(
            (Num(row.Mana) + Num(row.LvlMana) * (level - 1)) << CalcExpression.ShiftBits(Num(row.ManaShift)),
            Num(row.MinMana) << 8);

    private static long ElementalMin(Skills row, int level)
        => CalcExpression.Progression(Num(row.EMin), Num(row.EMinLev1), Num(row.EMinLev2), Num(row.EMinLev3),
            Num(row.EMinLev4), Num(row.EMinLev5), level);

    private static long ElementalMax(Skills row, int level)
        => CalcExpression.Progression(Num(row.EMax), Num(row.EMaxLev1), Num(row.EMaxLev2), Num(row.EMaxLev3),
            Num(row.EMaxLev4), Num(row.EMaxLev5), level);

    private static long PhysicalMin(Skills row, int level)
        => CalcExpression.Progression(Num(row.MinDam), Num(row.MinLevDam1), Num(row.MinLevDam2), Num(row.MinLevDam3),
            Num(row.MinLevDam4), Num(row.MinLevDam5), level);

    private static long PhysicalMax(Skills row, int level)
        => CalcExpression.Progression(Num(row.MaxDam), Num(row.MaxLevDam1), Num(row.MaxLevDam2), Num(row.MaxLevDam3),
            Num(row.MaxLevDam4), Num(row.MaxLevDam5), level);

    /// <summary>Applies the row's HitShift, turning a raw damage column into 256ths.</summary>
    private static long Shift(long raw, Skills row)
        => raw << CalcExpression.ShiftBits(row.HitShift ?? 8);

    private static long Num(string? raw) => CalcExpression.ParseLong(raw);

    private static long Num(int? raw) => CalcExpression.ParseLong(raw);

    /// <summary>
    /// The expression-valued columns <c>skillcalc.txt</c> aliases. Column name to DTO
    /// property is pure plumbing, so it stays in code rather than in config.
    /// </summary>
    internal static string? ExpressionColumn(Skills row, string column)
    {
        var lower = column.ToLowerInvariant();
        if (CalcExpression.TryParseIndexed(lower, "calc", 1, 10, out var index))
            return SkillCalcSource.Calc(row, index);
        if (CalcExpression.TryParseIndexed(lower, "passivecalc", 1, SkillCalcSource.PassiveStatSlots, out index))
            return SkillCalcSource.PassiveCalc(row, index);
        if (CalcExpression.TryParseIndexed(lower, "aurastatcalc", 1, SkillCalcSource.AuraStatSlots, out index))
            return SkillCalcSource.AuraStatCalc(row, index);

        return lower switch
        {
            "auralencalc" => row.AuraLenCalc,
            "aurarangecalc" => row.AuraRangeCalc,
            "petmax" => row.PetMax,
            "skpoints" => row.SkPoints,
            _ => null
        };
    }
}

/// <summary>
/// Evaluates the calc expressions that <c>skills.txt</c> and <c>skilldesc.txt</c> use to
/// describe how a skill scales — <c>ln12</c>, <c>enma</c>, <c>usmc</c>,
/// <c>min(1+(lvl-1)/2,14)</c>, <c>skill('Fire Bolt'.blvl)</c> and friends — at a fixed
/// skill level.
///
/// The symbol vocabulary comes from the game's own <c>skillcalc.txt</c> (see
/// <see cref="CalcCodeTable"/>), so column aliases and level progressions are data rather
/// than transcription; <see cref="SkillCalcContext"/> supplies only the quantities the
/// table names in prose.
///
/// Expressions the exporter cannot resolve fail rather than guess, and the caller is
/// expected to drop the line and report it. What still cannot be solved is <c>sctk</c>
/// (the channeling tick), which is derived from cast rate the export does not model.
/// <c>ulvl</c> (the caster's character level), <c>cond('Difficulty', …)</c>, <c>mps</c>
/// (mana per second) and a minion skill reached through
/// <c>sklvl('Tainted Fire Ball'.lvl.edmn)</c> all do resolve — see
/// <see cref="SkillCalcContext"/>.
///
/// The preview allocates points to exactly one skill — the one being described — and wears
/// no gear, which is what the game itself shows a character with no other points. So a
/// synergy term (<c>skill('Fire Ball'.blvl)</c>), a gear-accumulated stat
/// (<c>stat('extra_skelewarriors'.accr)</c>) and the mastery aggregates (<c>mael</c>,
/// <c>madm</c>, …) are all worth 0. The synergy list D2 renders in its own tooltip box is
/// exported alongside, so the website can show which skills feed this one without the
/// exporter having to guess an allocation.
///
/// "Allocated to exactly one skill" is not the same as "every cross-skill term is 0", and
/// the difference is load-bearing: a reference to another skill's *expression* column is
/// solved against that row at level 0 by
/// <see cref="SkillCalcContext.TryResolveUnallocatedSkill"/>, because Reimagined's synergy
/// columns count the referencing skill among their own operands. See that method.
/// </summary>
internal static class SkillCalculator
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="context"/>.
    /// Returns false (and <c>0</c>) for an empty expression, a syntax error, a division by
    /// zero, or any symbol the exporter cannot resolve.
    /// </summary>
    public static bool TryEvaluate(string? expression, SkillCalcContext context, out long value)
        => CalcExpression.TryEvaluate(expression, context, out value);
}
