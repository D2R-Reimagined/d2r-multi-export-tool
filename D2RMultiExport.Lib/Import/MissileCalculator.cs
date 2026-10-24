// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using Missiles = D2RReimaginedTools.Models.Missiles;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// A <c>missiles.txt</c> row evaluated at a fixed level — the scope behind
/// <c>miss('firewall'.rang)</c> and the <c>m1en</c>/<c>m2ex</c>/<c>m3rn</c> symbol families.
/// Skill-spawned missiles inherit the caster's skill level, so the level here is the level
/// of the skill whose description is being solved.
///
/// The vocabulary is <c>misscalc.txt</c>: the <c>parN</c>/<c>cpaN</c>/<c>hpaN</c>/<c>chpN</c>/
/// <c>dpaN</c>/<c>rang</c>/<c>rad</c>/<c>ccl1</c>/<c>scl1</c> codes come straight off the
/// table's column aliases, the <c>slXY</c>/<c>sdXY</c>/<c>clXY</c>/… progressions off its
/// formula rows, and only the derived damage quantities below are defined here.
/// </summary>
internal sealed class MissileCalcContext : ICalcScope
{
    public required Missiles Row { get; init; }
    public required GameData Data { get; init; }
    public required CalcLevels Levels { get; init; }

    /// <summary>
    /// The skill whose description spawned this missile, when there is one. Missile cells
    /// routinely name their own caster, so without the back-link those references fall
    /// through the isolated-preview policy and silently contribute nothing.
    /// </summary>
    public SkillCalcContext? Owner { get; init; }

    public CalcCodeTable Codes => Data.MissileCalcCodes;

    /// <summary>
    /// Shared with the skill that spawned this missile (and with any missile row this one
    /// chains to), so a failure deep inside a missile cell is reported against the skill
    /// description line that was being solved.
    /// </summary>
    public CalcTrace Trace { get; init; } = new();

    /// <summary>
    /// Shared with any missile row this one chains to, so a pair of <c>missiles.txt</c> rows
    /// that quote each other cannot recurse forever.
    /// </summary>
    public HashSet<string> Expanding { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    private int Level => Levels.Level;

    public bool TryResolveIntrinsic(string symbol, out long value)
    {
        value = 0;
        switch (symbol.ToLowerInvariant())
        {
            case "lvl":
                value = Level;
                return true;

            // The caster's character level, same quantity as skillcalc.txt's `ulvl`.
            case "ulvl":
                value = Levels.ReadCharacterLevel();
                return true;

            // Damage columns are stored pre-shift; `…ns`/`…xs` are the 256ths form the
            // per-frame formulas multiply back up, the plain spellings are what a player
            // sees. Both derive from the row's own HitShift.
            case "damn":
                value = Shift(PhysicalMin()) / 256;
                return true;
            case "damx":
                value = Shift(PhysicalMax()) / 256;
                return true;
            case "dmns":
                value = Shift(PhysicalMin());
                return true;
            case "dmxs":
                value = Shift(PhysicalMax());
                return true;
            case "edmn":
                value = Shift(ElementalMin()) / 256;
                return true;
            case "edmx":
                value = Shift(ElementalMax()) / 256;
                return true;
            case "edns":
                value = Shift(ElementalMin());
                return true;
            case "edxs":
                value = Shift(ElementalMax());
                return true;
            case "edln":
                value = CalcExpression.LengthProgression(
                    CalcExpression.ParseLong(Row.ElementalLength),
                    CalcExpression.ParseLong(Row.ElementalLevelLength1),
                    CalcExpression.ParseLong(Row.ElementalLevelLength2),
                    CalcExpression.ParseLong(Row.ElementalLevelLength3), Level);
                return true;

            // `fnum` is the missile's current animation frame — a property of a missile in
            // flight, not of the table row, so a static description cannot solve it.
            default:
                return false;
        }
    }

    public bool TryResolveColumn(string column, out long value)
    {
        value = 0;

        // Param columns are plain numbers; an empty cell is a zero, as in game.
        if (TryNumericColumn(column, out var numeric))
        {
            value = CalcExpression.ParseLong(numeric);
            return true;
        }

        // Range/Radius and the Srv/Clt calc columns hold expressions evaluated in this
        // missile's own scope; an empty cell means the description cannot be solved.
        var expression = column.ToLowerInvariant() switch
        {
            // The parser aliases the `Radius` column onto `LevelRange`.
            "radius" => Row.LevelRange,
            "range" => Row.Range,
            "srvcalc1" => Row.ServerCalculation1,
            "cltcalc1" => Row.ClientCalculation1,
            _ => null
        };
        return CalcExpression.TryEvaluateNested(column, expression, this, out value);
    }

    /// <summary>
    /// <c>misscalc.txt</c> defines no cross-reference functions of its own, but a missile's
    /// expression cells reach back at skills constantly, and most often at the very skill
    /// that fired them — <c>moltenboulderfirepath</c> reads
    /// <c>skill('Molten Boulder'.blvl)</c>, <c>sacrificehit</c> reads
    /// <c>skill('Sacrifice'.par8)</c>, the <c>arcticblast_*</c> chain reads
    /// <c>skill('Arctic Blast'.par5)</c>. Those resolve against <see cref="Owner"/> exactly
    /// as they would inside the skill's own calc column; a reference to any *other* skill
    /// still follows the isolated-preview policy.
    ///
    /// <c>miss('name'.field)</c> chains to a further missile row — Reimagined's cloud
    /// makers size their spawn off the cloud they spawn — keeping the same levels and owner.
    /// </summary>
    public bool TryResolveReference(string function, string target, List<string> fields, out long value)
    {
        value = 0;

        if (function.Equals("miss", StringComparison.OrdinalIgnoreCase))
        {
            if (fields.Count != 1) return false;
            if (!Data.Missiles.TryGetValue(target, out var row))
                return Trace.Fail($"missiles.txt has no row named '{target}'");

            var guard = $"miss:{target}";
            if (!Expanding.Add(guard)) return Trace.Fail($"miss('{target}'.…) refers back to itself");
            try
            {
                var scope = new MissileCalcContext
                {
                    Row = row,
                    Data = Data,
                    Levels = Levels,
                    Owner = Owner,
                    Expanding = Expanding,
                    Trace = Trace
                };
                return CalcExpression.TryResolveSymbol(fields[0], scope, out value);
            }
            finally
            {
                Expanding.Remove(guard);
            }
        }

        // A missile's conditions are evaluated against its source unit — the caster — so
        // they answer exactly as they would inside the skill's own calc column.
        if (function.Equals("cond", StringComparison.OrdinalIgnoreCase))
            return CalcExpression.TryResolveCondition(Data, target, fields, Trace, out value);

        if (Owner is not null
            && fields.Count == 1
            && (function.Equals("skill", StringComparison.OrdinalIgnoreCase)
                || function.Equals("sklvl", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(target, Owner.Skill.Skill, StringComparison.OrdinalIgnoreCase))
                return Owner.TryResolveOwnSymbol(fields[0], out value);

            // Any other skill has no points in an isolated preview, but its expression
            // columns can still reach back at the caster — Reimagined's synergy sums
            // include the referencing skill — so they are solved at level 0 rather than
            // dropped wholesale. Falls through to the flat-zero policy when that fails.
            if (function.Equals("skill", StringComparison.OrdinalIgnoreCase)
                && Owner.TryResolveUnallocatedSkill(target, fields[0], out value))
            {
                return true;
            }
        }

        return CalcExpression.TryResolveIsolatedSkill(
            Data, function, target, fields.Count == 1 ? fields[0] : null, out value);
    }

    /// <summary>
    /// The plain-integer parameter columns <c>misscalc.txt</c> aliases. Column name to DTO
    /// property is pure plumbing, so it stays in code rather than in config.
    /// </summary>
    private bool TryNumericColumn(string column, out int? value)
    {
        value = column.ToLowerInvariant() switch
        {
            "param1" => Row.Param1,
            "param2" => Row.Param2,
            "param3" => Row.Param3,
            "param4" => Row.Param4,
            "param5" => Row.Param5,
            "cltparam1" => Row.ClientParam1,
            "cltparam2" => Row.ClientParam2,
            "cltparam3" => Row.ClientParam3,
            "cltparam4" => Row.ClientParam4,
            "cltparam5" => Row.ClientParam5,
            "shitpar1" => Row.ServerHitParam1,
            "shitpar2" => Row.ServerHitParam2,
            "shitpar3" => Row.ServerHitParam3,
            "chitpar1" => Row.ClientHitParam1,
            "chitpar2" => Row.ClientHitParam2,
            "chitpar3" => Row.ClientHitParam3,
            "dparam1" => Row.DamageParam1,
            "dparam2" => Row.DamageParam2,
            _ => null
        };
        return IsNumericColumn(column);
    }

    private static bool IsNumericColumn(string column)
    {
        var lower = column.ToLowerInvariant();
        return CalcExpression.TryParseIndexed(lower, "param", 1, 5, out _)
            || CalcExpression.TryParseIndexed(lower, "cltparam", 1, 5, out _)
            || CalcExpression.TryParseIndexed(lower, "shitpar", 1, 3, out _)
            || CalcExpression.TryParseIndexed(lower, "chitpar", 1, 3, out _)
            || CalcExpression.TryParseIndexed(lower, "dparam", 1, 2, out _);
    }

    private long Shift(long raw) => raw << CalcExpression.ShiftBits(Row.HitShift ?? 8);

    private long PhysicalMin() => CalcExpression.Progression(
        CalcExpression.ParseLong(Row.MinDamage),
        CalcExpression.ParseLong(Row.MinLevelDamage1), CalcExpression.ParseLong(Row.MinLevelDamage2),
        CalcExpression.ParseLong(Row.MinLevelDamage3), CalcExpression.ParseLong(Row.MinLevelDamage4),
        CalcExpression.ParseLong(Row.MinLevelDamage5), Level);

    private long PhysicalMax() => CalcExpression.Progression(
        CalcExpression.ParseLong(Row.MaxDamage),
        CalcExpression.ParseLong(Row.MaxLevelDamage1), CalcExpression.ParseLong(Row.MaxLevelDamage2),
        CalcExpression.ParseLong(Row.MaxLevelDamage3), CalcExpression.ParseLong(Row.MaxLevelDamage4),
        CalcExpression.ParseLong(Row.MaxLevelDamage5), Level);

    private long ElementalMin() => CalcExpression.Progression(
        CalcExpression.ParseLong(Row.ElementalMin),
        CalcExpression.ParseLong(Row.MinElementalLevel1), CalcExpression.ParseLong(Row.MinElementalLevel2),
        CalcExpression.ParseLong(Row.MinElementalLevel3), CalcExpression.ParseLong(Row.MinElementalLevel4),
        CalcExpression.ParseLong(Row.MinElementalLevel5), Level);

    private long ElementalMax() => CalcExpression.Progression(
        CalcExpression.ParseLong(Row.ElementalMax),
        CalcExpression.ParseLong(Row.MaxElementalLevel1), CalcExpression.ParseLong(Row.MaxElementalLevel2),
        CalcExpression.ParseLong(Row.MaxElementalLevel3), CalcExpression.ParseLong(Row.MaxElementalLevel4),
        CalcExpression.ParseLong(Row.MaxElementalLevel5), Level);
}
