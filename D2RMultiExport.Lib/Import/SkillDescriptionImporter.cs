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
public sealed class SkillDescriptionImporter(GameData data, int bonusLevels)
{
    private readonly ImportResult<SkillDescriptionSet> _result = new();

    public Task<ImportResult<SkillDescriptionSet>> ImportAsync()
    {
        foreach (var skill in data.Skills.Values
            .Where(static skill => !string.IsNullOrWhiteSpace(skill.CharClass))
            .OrderBy(static skill => skill.Id))
        {
            if (!data.SkillDescs.TryGetValue(skill.SkillDesc ?? "", out var desc)) continue;

            var maxLevel = Math.Max(skill.MaxLevel, 1) + Math.Max(bonusLevels, 0);
            var set = new SkillDescriptionSet
            {
                SkillId = skill.Id,
                MaxLevel = maxLevel,
                Stats = Build(skill, desc.DescriptionLines, maxLevel),
                Details = Build(skill, desc.DetailLines, maxLevel),
                Synergies = Build(skill, desc.SynergyLines, maxLevel)
            };

            if (!set.IsEmpty) _result.AddItem(set);
        }

        return Task.FromResult(_result);
    }

    private List<SkillDescriptionLine> Build(SkillEntry skill, List<SkillDescLine> lines, int maxLevel)
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

            if (!TryBuildLine(skill, line, layout, maxLevel, out var built1))
            {
                _result.AddWarning("SkillDescription", skill.Skill,
                    $"Skipped '{line.TextA}' (function {line.Function}): could not evaluate "
                    + $"'{Describe(line)}'.");
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
        int maxLevel,
        out SkillDescriptionLine built)
    {
        built = new SkillDescriptionLine
        {
            Key = line.TextA!,
            Scale = ResolveDamageScale(skill, line)
        };

        if (layout.NameArg && !string.IsNullOrWhiteSpace(line.TextB))
            built.Args = [line.TextB];
        else if (layout.Plural && !string.IsNullOrWhiteSpace(line.TextB))
            built.PluralKey = line.TextB;

        var slots = SlotCount(layout.Layout, line);
        if (slots == 0) return true;

        var tables = new List<double>[slots];
        for (var slot = 0; slot < slots; slot++) tables[slot] = new List<double>(maxLevel);

        for (var level = 1; level <= maxLevel; level++)
        {
            var context = new SkillCalcContext { Skill = skill, Data = data, Level = level };
            if (!TryEvaluateSlots(line, layout.Layout, context, out var values)) return false;
            if (values.Length != slots) return false;
            for (var slot = 0; slot < slots; slot++) tables[slot].Add(values[slot]);
        }

        built.Values = [.. tables.Select(Trim)];
        return true;
    }

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

    private static string Describe(SkillDescLine line)
        => string.IsNullOrWhiteSpace(line.CalcB) ? line.CalcA ?? "" : $"{line.CalcA} / {line.CalcB}";

    private static string? ResolveDamageScale(SkillEntry skill, SkillDescLine line)
    {
        if (ReferencesAny(skill, [line.CalcA, line.CalcB], ["pnma", "pxma", "pnms", "pxms"]))
            return "physical";
        if (ReferencesAny(skill, [line.CalcA, line.CalcB], ["edmn", "edmx", "edns", "edxs", "enma", "exma", "enms", "exms"]))
            return "elemental";
        if (ReferencesAny(skill, [line.CalcA, line.CalcB], ["edln"]))
            return "elementalLength";
        return null;
    }

    private static bool ReferencesAny(
        SkillEntry skill,
        IEnumerable<string?> expressions,
        IReadOnlyCollection<string> symbols)
    {
        var pending = new Stack<string>(expressions.Where(static value => !string.IsNullOrWhiteSpace(value))!);
        var visitedCalcs = new HashSet<int>();
        while (pending.TryPop(out var expression))
        {
            var tokens = System.Text.RegularExpressions.Regex.Matches(expression, @"\b[a-zA-Z]+\d*\b")
                .Select(static match => match.Value);
            foreach (var token in tokens)
            {
                if (symbols.Contains(token, StringComparer.OrdinalIgnoreCase)) return true;
                if (!token.StartsWith("clc", StringComparison.OrdinalIgnoreCase)
                    || !int.TryParse(token[3..], out var index)
                    || index is < 1 or > 10
                    || !visitedCalcs.Add(index)) continue;
                var nested = SkillCalcSource.Calc(skill.SourceRow, index);
                if (!string.IsNullOrWhiteSpace(nested)) pending.Push(nested);
            }
        }
        return false;
    }
}
