// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Turns each class skill's <c>skilldesc.txt</c> description lines into the per-level
/// <see cref="SkillDescriptionSet"/> the website renders under a skill node — "Fire Damage:
/// 3-6", "Mana Cost: 2.5", "Receives Bonuses From: …" — solved once for every level the
/// planner can display.
///
/// Layout comes from <see cref="SkillDescFunctionConfig"/>; the numbers come from
/// <see cref="SkillCalculator"/>. A line whose calc the evaluator cannot resolve is dropped
/// and reported as a warning rather than shipped with a guessed number.
/// </summary>
public sealed class SkillDescriptionImporter(GameData data)
{
    private readonly ImportResult<SkillDescriptionSet> _result = new();

    /// <summary>
    /// Highest character level the <c>ulvl</c> axis is solved to, and the level the flat
    /// tables of a <c>ulvl</c>-sensitive line are solved at.
    /// </summary>
    private readonly int _characterLevels = Math.Max(data.ExportConfig.SkillDescriptionCharacterLevels, 1);

    public Task<ImportResult<SkillDescriptionSet>> ImportAsync()
    {
        foreach (var skill in data.Skills.Values
            .Where(static skill => !string.IsNullOrWhiteSpace(skill.CharClass))
            .OrderBy(static skill => skill.Id))
        {
            if (!data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var desc)) continue;

            // A character can only spend `maxlvl` hard points, but +skills gear pushes the
            // effective level far past that, so the tables run to the configured ceiling
            // and the bonus axis takes whatever is left over.
            var hardCap = Math.Max(skill.MaxLevel, 1);
            var maxLevel = Math.Max(data.ExportConfig.SkillDescriptionMaxLevel, hardCap);
            var bonus = maxLevel - hardCap;
            var set = new SkillDescriptionSet
            {
                SkillId = skill.Id,
                MaxLevel = maxLevel,
                HardPointCap = hardCap,
                CharacterLevelCap = _characterLevels,
                Stats = Build(skill, desc.DescriptionLines, hardCap, bonus),
                Details = Build(skill, desc.DetailLines, hardCap, bonus),
                Synergies = Build(skill, desc.SynergyLines, hardCap, bonus)
            };

            if (!set.IsEmpty) _result.AddItem(set);
        }

        return Task.FromResult(_result);
    }

    private List<SkillDescriptionLine> Build(SkillEntry skill, List<SkillDescLine> lines, int hardCap, int bonusCap)
    {
        var built = new List<SkillDescriptionLine>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.TextA)) continue;

            var layout = SkillDescFunctionConfig.Resolve(line.Function);
            if (layout is null)
            {
                _result.AddWarning("SkillDescription", skill.Skill,
                    $"Unmapped skilldesc description function {line.Function} on '{line.TextA}' — "
                    + "add it to Config/skill-desc-functions.json.");
                continue;
            }

            if (!TryBuildLine(skill, line, layout, hardCap, bonusCap, out var built1, out var reason))
            {
                _result.AddWarning("SkillDescription", skill.Skill,
                    $"Skipped '{line.TextA}' (function {line.Function}): could not evaluate "
                    + $"'{Describe(line)}' — {reason ?? "no reason recorded"}.");
                continue;
            }

            ProbeTranslations(built1);
            built.Add(built1);
        }
        return built;
    }

    /// <summary>
    /// Touches every key the line will emit so a template or skill name the mod never
    /// translated lands in <c>extras/missing-translations.txt</c> instead of showing up on
    /// the website as a bare key.
    /// </summary>
    private void ProbeTranslations(SkillDescriptionLine line)
    {
        data.Translations.GetValue(line.Key);
        if (line.PluralKey is not null) data.Translations.GetValue(line.PluralKey);
        foreach (var arg in line.Args ?? []) data.Translations.GetValue(arg);
    }

    private bool TryBuildLine(
        SkillEntry skill,
        SkillDescLine line,
        SkillDescFunction layout,
        int hardCap,
        int bonusCap,
        out SkillDescriptionLine built,
        out string? reason)
    {
        reason = null;
        built = new SkillDescriptionLine { Key = line.TextA! };

        if (layout.NameArg && !string.IsNullOrWhiteSpace(line.TextB))
            built.Args = [line.TextB];
        else if (layout.Plural && !string.IsNullOrWhiteSpace(line.TextB))
            built.PluralKey = line.TextB;

        var slots = SlotCount(layout.Layout, line);
        if (slots == 0) return true;

        var maxLevel = hardCap + bonusCap;
        var tables = new List<double>[slots];
        for (var slot = 0; slot < slots; slot++) tables[slot] = new List<double>(maxLevel);

        // First pass: the flat per-effective-level table, every level treated as a hard
        // point. This is what the overwhelming majority of lines need, and it is also what
        // tells us whether the line reads `blvl` at all.
        var hardSoftSensitive = false;
        var characterLevelSensitive = false;
        var reads = new List<(ICalcScope Scope, string Symbol)>();
        for (var level = 1; level <= maxLevel; level++)
        {
            var levels = new CalcLevels(level, level, _characterLevels);
            var context = Context(skill, levels, (scope, symbol) => reads.Add((scope, symbol)));
            if (!TryEvaluateSlots(line, layout.Layout, context, out var values))
            {
                reason = context.Trace.Reason;
                return false;
            }
            if (values.Length != slots)
            {
                reason = $"the description function wants {slots} value(s) but the row supplies {values.Length}";
                return false;
            }
            for (var slot = 0; slot < slots; slot++) tables[slot].Add(values[slot]);
            hardSoftSensitive |= levels.UsedBaseLevel;
            characterLevelSensitive |= levels.UsedCharacterLevel;
        }

        built.Values = [.. tables.Select(Trim)];
        ApplyDamageScale(skill, built, reads);

        if (hardSoftSensitive)
        {
            var grid = BuildGrid(skill, line, layout, hardCap, bonusCap, slots);
            if (grid.Count > 0) built.Values2D = grid;
        }

        if (characterLevelSensitive)
        {
            var grid = BuildCharacterLevelGrid(skill, line, layout, maxLevel, slots);
            if (grid.Count > 0) built.ValuesUlvl = grid;
        }

        return true;
    }

    /// <summary>
    /// Re-solves a <c>ulvl</c>-sensitive line over the (skill level, character level) grid.
    /// The caster's character level is a third quantity neither skill-level axis can stand
    /// in for — <c>Thorns</c>' returned damage is <c>edmn * (ulvl + 100)/100</c>, so a
    /// level-1 Paladin and a level-99 one read the same tooltip line differently at the same
    /// skill level. A save-file viewer knows the number exactly; a planner needs an input.
    /// </summary>
    private List<double[][]> BuildCharacterLevelGrid(
        SkillEntry skill,
        SkillDescLine line,
        SkillDescFunction layout,
        int maxLevel,
        int slots)
    {
        var grids = new List<double[]>[slots];
        for (var slot = 0; slot < slots; slot++) grids[slot] = new List<double[]>(maxLevel);

        for (var level = 1; level <= maxLevel; level++)
        {
            var rows = new List<double>[slots];
            for (var slot = 0; slot < slots; slot++) rows[slot] = new List<double>(_characterLevels);

            for (var character = 1; character <= _characterLevels; character++)
            {
                var levels = new CalcLevels(level, level, character);
                if (!TryEvaluateSlots(line, layout.Layout, Context(skill, levels), out var values)
                    || values.Length != slots)
                {
                    // The flat pass already succeeded at every level, so this cannot
                    // normally happen; fall back to the flat table rather than ship a hole.
                    return [];
                }
                for (var slot = 0; slot < slots; slot++) rows[slot].Add(values[slot]);
            }

            for (var slot = 0; slot < slots; slot++) grids[slot].Add(Trim(rows[slot]));
        }

        return [.. grids.Select(TrimRows)];
    }

    /// <summary>
    /// Re-solves a <c>blvl</c>-sensitive line over the (hard points, bonus levels) grid.
    /// D2 pays soft points from <c>+skills</c> gear at a different rate than points the
    /// character spent, so one number per effective level cannot describe the line — a
    /// geared character and a fully invested one land on the same index but not the same
    /// value.
    ///
    /// Row <c>0</c> is kept deliberately: an oskill granted purely by an item has no hard
    /// points, and the game renders it as all-soft.
    /// </summary>
    private List<double[][]> BuildGrid(
        SkillEntry skill,
        SkillDescLine line,
        SkillDescFunction layout,
        int hardCap,
        int bonusCap,
        int slots)
    {
        var grids = new List<double[]>[slots];
        for (var slot = 0; slot < slots; slot++) grids[slot] = new List<double[]>(hardCap + 1);

        for (var hard = 0; hard <= hardCap; hard++)
        {
            var rowBonusCap = hardCap + bonusCap - hard;
            var rows = new List<double>[slots];
            for (var slot = 0; slot < slots; slot++) rows[slot] = new List<double>(rowBonusCap + 1);

            for (var bonus = 0; bonus <= rowBonusCap; bonus++)
            {
                // A skill at zero total levels is not castable; the game still solves its
                // tooltip at level 1, and so does the flat table, so match that.
                var levels = new CalcLevels(Math.Max(hard + bonus, 1), hard, _characterLevels);
                if (!TryEvaluateSlots(line, layout.Layout, Context(skill, levels), out var values)
                    || values.Length != slots)
                {
                    // The flat pass already succeeded at every level, so this cannot
                    // normally happen; fall back to the flat table rather than ship a hole.
                    return [];
                }
                for (var slot = 0; slot < slots; slot++) rows[slot].Add(values[slot]);
            }

            for (var slot = 0; slot < slots; slot++) grids[slot].Add(Trim(rows[slot]));
        }

        return [.. grids.Select(TrimRows)];
    }

    private SkillCalcContext Context(SkillEntry skill, CalcLevels levels, Action<ICalcScope, string>? observer = null)
        => new() { Skill = skill, Data = data, Levels = levels, Trace = new CalcTrace { ObserveIntrinsic = observer } };

    /// <summary>
    /// Solves one level's worth of arguments for a description function. Note that the
    /// divisor is applied here, in floating point: the game's own tooltip shows fractional
    /// mana costs and durations ("Mana Cost: 2.5"), which is exactly this division.
    /// </summary>
    private static bool TryEvaluateSlots(
        SkillDescLine line,
        string layout,
        SkillCalcContext context,
        out double[] values)
    {
        values = [];

        switch (layout)
        {
            case "singleValue":
            {
                if (!SkillCalculator.TryEvaluate(line.CalcA, context, out var a)) return false;
                values = [a];
                return true;
            }
            case "valueWithDivisor":
            {
                if (!SkillCalculator.TryEvaluate(line.CalcA, context, out var a)) return false;
                // An empty divisor column means "no scaling", not "divide by zero".
                if (string.IsNullOrWhiteSpace(line.CalcB))
                {
                    values = [a];
                    return true;
                }
                if (!SkillCalculator.TryEvaluate(line.CalcB, context, out var divisor)) return false;
                if (divisor == 0) return false;
                values = [Round(a / (double)divisor)];
                return true;
            }
            case "twoValues":
            {
                if (!SkillCalculator.TryEvaluate(line.CalcA, context, out var a)) return false;
                // Some templates a two-value function points at only carry one %d
                // ("%s: +1%% Damage per %d Max Life"), and the row leaves calcB empty.
                if (string.IsNullOrWhiteSpace(line.CalcB))
                {
                    values = [a];
                    return true;
                }
                if (!SkillCalculator.TryEvaluate(line.CalcB, context, out var b)) return false;
                values = [a, b];
                return true;
            }
            case "sumOfValues":
            {
                if (!SkillCalculator.TryEvaluate(line.CalcA, context, out var a)) return false;
                // The second half is an optional bonus term (e.g. a summon's life bonus
                // from its mastery skill); an empty column means zero, not a failure.
                if (string.IsNullOrWhiteSpace(line.CalcB))
                {
                    values = [a];
                    return true;
                }
                if (!SkillCalculator.TryEvaluate(line.CalcB, context, out var b)) return false;
                values = [a + b];
                return true;
            }
            default:
                return false;
        }
    }

    private static int SlotCount(string layout, SkillDescLine line) => layout switch
    {
        "singleValue" => 1,
        "valueWithDivisor" => 1,
        "sumOfValues" => 1,
        "twoValues" => string.IsNullOrWhiteSpace(line.CalcB) ? 1 : 2,
        _ => 0
    };

    /// <summary>
    /// D2 renders one decimal place on the values its divisors produce; anything finer is
    /// an artefact of the division rather than something the game would show.
    /// </summary>
    private static double Round(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Drops the constant tail of a per-level table. Most lines stop scaling (or never
    /// scaled at all), and consumers clamp the level index to the last element, so keeping
    /// the repeats would only pad the bundle.
    /// </summary>
    private static double[] Trim(List<double> values)
    {
        var last = values.Count - 1;
        while (last > 0 && values[last].Equals(values[last - 1])) last--;
        return [.. values.Take(last + 1)];
    }

    /// <summary>The same trim applied to the hard-point axis of a grid.</summary>
    private static double[][] TrimRows(List<double[]> rows)
    {
        var last = rows.Count - 1;
        while (last > 0 && rows[last].AsSpan().SequenceEqual(rows[last - 1])) last--;
        return [.. rows.Take(last + 1)];
    }

    private static string Describe(SkillDescLine line)
        => string.IsNullOrWhiteSpace(line.CalcB) ? line.CalcA ?? "" : $"{line.CalcA} / {line.CalcB}";

    /// <summary>
    /// Works out which <c>*SymPerCalc</c> percentage applies to a line, so the website can
    /// re-evaluate it against live ranks instead of the exporter guessing an allocation.
    ///
    /// A line that reads the skill's own damage columns scales by the skill's own column,
    /// named by <see cref="SkillDescriptionLine.Scale"/> alone. A line whose damage comes
    /// from a *missile* — most of Reimagined's headline spells deal their damage through
    /// one — scales by that missile's column instead, which lives on the
    /// <c>missiles.txt</c> row and is otherwise never exported: without it the website
    /// applies no bonus at all and the synergy terms inside it
    /// (<c>moltenboulderfirepath</c>'s <c>skill('Summon Fenris'.blvl)</c>,
    /// <c>royalstrikemeteor</c>'s <c>skill('Fists of Fire'.blvl)</c>) silently count zero.
    /// </summary>
    private static void ApplyDamageScale(
        SkillEntry skill,
        SkillDescriptionLine built,
        List<(ICalcScope Scope, string Symbol)> reads)
    {
        // Observe the existing evaluator: it follows table aliases/formulas and quoted
        // references in their real scopes, with the same recursion guards and diagnostics.
        foreach (var (scope, symbol) in reads)
        {
            string? scale = scope is MissileCalcContext
                ? MissilePhysicalFields.Contains(symbol) ? "physical"
                    : MissileElementalFields.Contains(symbol) ? "elemental" : null
                : PhysicalSymbols.Contains(symbol) ? "physical"
                    : ElementalSymbols.Contains(symbol) ? "elemental"
                    : symbol.Equals("edln", StringComparison.OrdinalIgnoreCase) ? "elementalLength" : null;
            if (scale is null) continue;

            if (scope is SkillCalcContext skillScope)
            {
                if (skillScope.Skill.Id == skill.Id)
                {
                    built.Scale = scale;
                    return;
                }
                var source = skillScope.Skill.SourceRow;
                var percentage = scale switch
                {
                    "physical" => source?.DmgSymPerCalc,
                    "elemental" => source?.EDmgSymPerCalc,
                    _ => source?.ELenSymPerCalc
                };
                if (string.IsNullOrWhiteSpace(percentage)) continue;
                // Bind the other row's parameters before the client evaluates this in
                // the describing skill's context. Keep quoted synergy references live.
                var context = new SkillCalcContext { Skill = skillScope.Skill, Data = skillScope.Data, Levels = skillScope.Levels };
                if (!CalcExpression.TryBindParameters(percentage, context, out var bound)) continue;
                built.Scale = scale;
                built.ScaleCalc = bound;
                return;
            }

            var calc = scope switch
            {
                MissileCalcContext missile => scale == "physical"
                    ? missile.Row.DamageSymPerCalculation : missile.Row.ElementalDamageSymPerCalculation,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(calc)) continue;
            built.Scale = scale;
            built.ScaleCalc = calc.Trim().Trim('"');
            return;
        }
    }

    /// <summary>The skill-row damage symbols, per <c>skillcalc.txt</c>.</summary>
    private static readonly HashSet<string> PhysicalSymbols =
        new(["pnma", "pxma", "pnms", "pxms"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ElementalSymbols =
        new(["edmn", "edmx", "edns", "edxs", "enma", "exma", "enms", "exms"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MissileElementalFields =
        new(["edmn", "edmx", "edns", "edxs"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> MissilePhysicalFields =
        new(["damn", "damx", "dmns", "dmxs"], StringComparer.OrdinalIgnoreCase);
}
