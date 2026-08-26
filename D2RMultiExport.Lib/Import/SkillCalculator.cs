// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// The skill level a <see cref="SkillCalculator"/> evaluation is anchored to, plus the
/// lookup tables it needs to resolve cross-skill references.
/// </summary>
public sealed class SkillCalcContext
{
    public required SkillEntry Skill { get; init; }
    public required GameData Data { get; init; }
    public required int Level { get; init; }

    /// <summary>
    /// Symbols currently being expanded (<c>clc1</c>, <c>pst2</c>, …). Guards the
    /// self-referential calc chains D2R ships (a <c>Calc</c> column that mentions
    /// another <c>Calc</c> column that mentions the first) from recursing forever.
    /// </summary>
    internal HashSet<string> Expanding { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Evaluates the calc expressions that <c>skills.txt</c> and <c>skilldesc.txt</c> use to
/// describe how a skill scales — <c>ln12</c>, <c>enma</c>, <c>usmc</c>,
/// <c>min(1+(lvl-1)/2,14)</c>, <c>skill('Fire Bolt'.blvl)</c> and friends — at a fixed
/// skill level.
///
/// The game evaluates these with 32-bit integer arithmetic (division truncates), so this
/// evaluator does too; the fractional part players see on mana costs and durations comes
/// from the description function's divisor column, applied afterwards by
/// <see cref="SkillDescriptionImporter"/>.
///
/// Expressions the exporter cannot resolve fail rather than guess. The two families that
/// always fail are cross-file references (<c>miss('firewall'.rang)</c> reaches into
/// missiles.txt, which the export does not load) and another skill's *allocated* level
/// (<c>sklvl('Tainted Fire Ball'.lvl.edmn)</c>), which only exists for a live character.
/// A caller that gets <c>false</c> is expected to drop the line and report it.
///
/// Synergy terms — <c>skill('Fire Ball'.blvl)</c> on a *different* skill — resolve to 0,
/// so exported values are the skill's own contribution. The synergy list D2 renders in its
/// own tooltip box (description function 40/76/77/79) is exported alongside, so the
/// website can show which skills feed this one without the exporter having to guess an
/// allocation.
/// </summary>
internal static class SkillCalculator
{
    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="context"/>.
    /// Returns false (and <c>0</c>) for an empty expression, a syntax error, a division by
    /// zero, or any symbol the exporter cannot resolve.
    /// </summary>
    public static bool TryEvaluate(string? expression, SkillCalcContext context, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression)) return false;

        // Mod authors quote calc cells that contain a comma so the .txt column survives
        // a spreadsheet round-trip (e.g. "min(lvl*5,100)"); the quotes are not syntax.
        var text = expression.Trim().Trim('"');
        if (text.Length == 0) return false;

        var parser = new Parser(text, context);
        if (!parser.TryParseExpression(out var result)) return false;
        if (!parser.AtEnd) return false;

        value = result;
        return true;
    }

    /// <summary>
    /// D2's five-bracket level progression: the base column plus one per-level column for
    /// levels 2–8, 9–16, 17–22, 23–28, and 29+. Shared by the physical (<c>MinDam</c>),
    /// elemental (<c>EMin</c>) and to-hit damage columns.
    /// </summary>
    private static long Progression(long baseValue, long l1, long l2, long l3, long l4, long l5, int level)
        => baseValue
            + l1 * Math.Clamp(level - 1, 0, 7)
            + l2 * Math.Clamp(level - 8, 0, 8)
            + l3 * Math.Clamp(level - 16, 0, 6)
            + l4 * Math.Clamp(level - 22, 0, 6)
            + l5 * Math.Max(level - 28, 0);

    /// <summary>
    /// The three-bracket variant used by the elemental *length* columns (<c>ELen</c> +
    /// <c>ELevLen1..3</c>), which cover levels 2–8, 9–16 and 17+.
    /// </summary>
    private static long LengthProgression(long baseValue, long l1, long l2, long l3, int level)
        => baseValue
            + l1 * Math.Clamp(level - 1, 0, 7)
            + l2 * Math.Clamp(level - 8, 0, 8)
            + l3 * Math.Max(level - 16, 0);

    /// <summary>
    /// Resolves one bare calc symbol. Returns false for anything outside the vocabulary
    /// the exporter understands (see the class remarks).
    /// </summary>
    private static bool TryResolveSymbol(string symbol, SkillCalcContext context, out long value)
    {
        value = 0;
        var row = context.Skill.SourceRow;
        if (row is null) return false;

        var level = context.Level;

        switch (symbol.ToLowerInvariant())
        {
            // Skill level. The exporter renders a skill in isolation, so the "base"
            // (hard-point) level and the effective level are the same number.
            case "lvl":
            case "blvl":
            case "sklvl":
                value = level;
                return true;

            // Attack rating bonus: ToHit plus LevToHit per level past the first.
            case "toht":
                value = ParseLong(row.ToHit) + ParseLong(row.LevToHit) * (level - 1);
                return true;

            // Mana cost in 256ths — the form every description function divides by 256.
            case "usmc":
                value = Math.Max(
                    (ParseLong(row.Mana) + ParseLong(row.LvlMana) * (level - 1)) << ShiftBits(ParseLong(row.ManaShift)),
                    ParseLong(row.MinMana) << 8);
                return true;

            // Aura/state columns, evaluated in this skill's own context.
            case "len":
                return TryEvaluateNested(symbol, row.AuraLenCalc, context, out value);
            case "rng":
                return TryEvaluateNested(symbol, row.AuraRangeCalc, context, out value);
            case "pets":
                return TryEvaluateNested(symbol, row.PetMax, context, out value);

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

            // Elemental length in frames (cold length, poison duration).
            case "edln":
                value = LengthProgression(
                    ParseLong(row.ELen), ParseLong(row.ELevLen1), ParseLong(row.ELevLen2),
                    ParseLong(row.ELevLen3), level);
                return true;
        }

        // parN / paNN — a Param column, constant across levels.
        if (TryParseIndexed(symbol, "par", 1, 9, out var paramIndex)
            || TryParseIndexed(symbol, "pa", 10, 20, out paramIndex))
        {
            value = ParseLong(Param(row, paramIndex));
            return true;
        }

        // clcN / pstN / astN — another column on this row, expanded in place.
        if (TryParseIndexed(symbol, "clc", 1, 10, out var index))
            return TryEvaluateNested(symbol, Calc(row, index), context, out value);
        if (TryParseIndexed(symbol, "pst", 1, 14, out index))
            return TryEvaluateNested(symbol, PassiveCalc(row, index), context, out value);
        if (TryParseIndexed(symbol, "ast", 1, 6, out index))
            return TryEvaluateNested(symbol, AuraStatCalc(row, index), context, out value);

        // lnXY / dmXY — "ParamX at level 1, ParamY per level after that". The two forms
        // behave identically: every mod row that uses `dm` does so for a duration or a
        // percentage, never for a damage column that would want the hit shift.
        if (TryParseParamPair(symbol, "ln", out var first, out var second)
            || TryParseParamPair(symbol, "dm", out first, out second))
        {
            value = ParseLong(Param(row, first)) + ParseLong(Param(row, second)) * (level - 1);
            return true;
        }

        return false;
    }

    private static long ElementalMin(D2RReimaginedTools.Models.Skills row, int level)
        => Progression(row.EMin ?? 0, row.EMinLev1 ?? 0, row.EMinLev2 ?? 0, row.EMinLev3 ?? 0,
            row.EMinLev4 ?? 0, row.EMinLev5 ?? 0, level);

    private static long ElementalMax(D2RReimaginedTools.Models.Skills row, int level)
        => Progression(row.EMax ?? 0, row.EMaxLev1 ?? 0, row.EMaxLev2 ?? 0, row.EMaxLev3 ?? 0,
            row.EMaxLev4 ?? 0, row.EMaxLev5 ?? 0, level);

    private static long PhysicalMin(D2RReimaginedTools.Models.Skills row, int level)
        => Progression(row.MinDam ?? 0, row.MinLevDam1 ?? 0, row.MinLevDam2 ?? 0, row.MinLevDam3 ?? 0,
            row.MinLevDam4 ?? 0, row.MinLevDam5 ?? 0, level);

    private static long PhysicalMax(D2RReimaginedTools.Models.Skills row, int level)
        => Progression(row.MaxDam ?? 0, row.MaxLevDam1 ?? 0, row.MaxLevDam2 ?? 0, row.MaxLevDam3 ?? 0,
            row.MaxLevDam4 ?? 0, row.MaxLevDam5 ?? 0, level);

    /// <summary>Applies the row's HitShift, turning a raw damage column into 256ths.</summary>
    private static long Shift(long raw, D2RReimaginedTools.Models.Skills row)
        => raw << ShiftBits(row.HitShift ?? 8);

    /// <summary>Keeps a shift column inside a range <c>&lt;&lt;</c> can act on.</summary>
    private static int ShiftBits(long raw) => (int)Math.Clamp(raw, 0, 32);

    private static bool TryEvaluateNested(
        string symbol,
        string? expression,
        SkillCalcContext context,
        out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        if (!context.Expanding.Add(symbol)) return false;
        try
        {
            return TryEvaluate(expression, context, out value);
        }
        finally
        {
            context.Expanding.Remove(symbol);
        }
    }

    /// <summary>Matches <c>prefix</c> followed by a single index inside the given bounds.</summary>
    private static bool TryParseIndexed(string symbol, string prefix, int min, int max, out int index)
    {
        index = 0;
        if (!symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var digits = symbol[prefix.Length..];
        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;
        return index >= min && index <= max;
    }

    /// <summary>Matches the two-digit <c>lnXY</c> / <c>dmXY</c> param-pair spelling.</summary>
    private static bool TryParseParamPair(string symbol, string prefix, out int first, out int second)
    {
        first = second = 0;
        if (symbol.Length != prefix.Length + 2) return false;
        if (!symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (!char.IsAsciiDigit(symbol[^2]) || !char.IsAsciiDigit(symbol[^1])) return false;
        first = symbol[^2] - '0';
        second = symbol[^1] - '0';
        return first >= 1 && second >= 1;
    }

    private static long ParseLong(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static long ParseLong(int? raw) => raw ?? 0;

    private static string? Param(D2RReimaginedTools.Models.Skills row, int index) => index switch
    {
        1 => row.Param1, 2 => row.Param2, 3 => row.Param3, 4 => row.Param4, 5 => row.Param5,
        6 => row.Param6, 7 => row.Param7, 8 => row.Param8, 9 => row.Param9, 10 => row.Param10,
        11 => row.Param11, 12 => row.Param12, 13 => row.Param13, 14 => row.Param14, 15 => row.Param15,
        16 => row.Param16, 17 => row.Param17, 18 => row.Param18, 19 => row.Param19, 20 => row.Param20,
        _ => null
    };

    private static string? Calc(D2RReimaginedTools.Models.Skills row, int index) => index switch
    {
        1 => row.Calc1, 2 => row.Calc2, 3 => row.Calc3, 4 => row.Calc4, 5 => row.Calc5,
        6 => row.Calc6, 7 => row.Calc7, 8 => row.Calc8, 9 => row.Calc9, 10 => row.Calc10,
        _ => null
    };

    private static string? PassiveCalc(D2RReimaginedTools.Models.Skills row, int index) => index switch
    {
        1 => row.PassiveCalc1, 2 => row.PassiveCalc2, 3 => row.PassiveCalc3, 4 => row.PassiveCalc4,
        5 => row.PassiveCalc5, 6 => row.PassiveCalc6, 7 => row.PassiveCalc7, 8 => row.PassiveCalc8,
        9 => row.PassiveCalc9, 10 => row.PassiveCalc10, 11 => row.PassiveCalc11, 12 => row.PassiveCalc12,
        13 => row.PassiveCalc13, 14 => row.PassiveCalc14,
        _ => null
    };

    private static string? AuraStatCalc(D2RReimaginedTools.Models.Skills row, int index) => index switch
    {
        1 => row.AuraStatCalc1, 2 => row.AuraStatCalc2, 3 => row.AuraStatCalc3,
        4 => row.AuraStatCalc4, 5 => row.AuraStatCalc5, 6 => row.AuraStatCalc6,
        _ => null
    };

    /// <summary>
    /// Recursive-descent parser over one calc expression. Precedence, loosest first:
    /// ternary <c>?:</c>, comparison, additive, multiplicative, unary minus, primary.
    /// </summary>
    private sealed class Parser(string text, SkillCalcContext context)
    {
        private int _position;

        public bool AtEnd
        {
            get
            {
                SkipWhitespace();
                return _position >= text.Length;
            }
        }

        public bool TryParseExpression(out long value)
        {
            if (!TryParseComparison(out var condition))
            {
                value = 0;
                return false;
            }

            SkipWhitespace();
            if (!Match('?'))
            {
                value = condition;
                return true;
            }

            value = 0;
            if (!TryParseExpression(out var whenTrue)) return false;
            SkipWhitespace();
            if (!Match(':')) return false;
            if (!TryParseExpression(out var whenFalse)) return false;

            value = condition != 0 ? whenTrue : whenFalse;
            return true;
        }

        private bool TryParseComparison(out long value)
        {
            if (!TryParseAdditive(out value)) return false;

            SkipWhitespace();
            var op = PeekComparisonOperator();
            if (op is null) return true;

            _position += op.Length;
            if (!TryParseAdditive(out var right))
            {
                value = 0;
                return false;
            }

            value = op switch
            {
                ">" => value > right ? 1 : 0,
                "<" => value < right ? 1 : 0,
                ">=" => value >= right ? 1 : 0,
                "<=" => value <= right ? 1 : 0,
                "==" => value == right ? 1 : 0,
                _ => value != right ? 1 : 0
            };
            return true;
        }

        private string? PeekComparisonOperator()
        {
            if (_position >= text.Length) return null;
            var remaining = text.AsSpan(_position);
            if (remaining.StartsWith(">=")) return ">=";
            if (remaining.StartsWith("<=")) return "<=";
            if (remaining.StartsWith("==")) return "==";
            if (remaining.StartsWith("!=")) return "!=";
            // A lone '=' is the D2 spelling of equality in a handful of rows.
            if (remaining[0] == '=') return "==";
            if (remaining[0] == '>') return ">";
            if (remaining[0] == '<') return "<";
            return null;
        }

        private bool TryParseAdditive(out long value)
        {
            if (!TryParseMultiplicative(out value)) return false;

            while (true)
            {
                SkipWhitespace();
                // Don't swallow the '>' of '->' style comparisons; only +/- continue here.
                if (_position >= text.Length) return true;
                var op = text[_position];
                if (op != '+' && op != '-') return true;

                _position++;
                if (!TryParseMultiplicative(out var right))
                {
                    value = 0;
                    return false;
                }
                value = op == '+' ? value + right : value - right;
            }
        }

        private bool TryParseMultiplicative(out long value)
        {
            if (!TryParseUnary(out value)) return false;

            while (true)
            {
                SkipWhitespace();
                if (_position >= text.Length) return true;
                var op = text[_position];
                if (op != '*' && op != '/') return true;

                _position++;
                if (!TryParseUnary(out var right))
                {
                    value = 0;
                    return false;
                }
                if (op == '/')
                {
                    if (right == 0)
                    {
                        value = 0;
                        return false;
                    }
                    value /= right;
                }
                else
                {
                    value *= right;
                }
            }
        }

        private bool TryParseUnary(out long value)
        {
            SkipWhitespace();
            if (Match('-'))
            {
                if (!TryParseUnary(out value)) return false;
                value = -value;
                return true;
            }
            if (Match('+')) return TryParseUnary(out value);
            return TryParsePrimary(out value);
        }

        private bool TryParsePrimary(out long value)
        {
            value = 0;
            SkipWhitespace();
            if (_position >= text.Length) return false;

            if (text[_position] == '(')
            {
                _position++;
                if (!TryParseExpression(out value)) return false;
                SkipWhitespace();
                return Match(')');
            }

            if (char.IsAsciiDigit(text[_position]))
            {
                var start = _position;
                while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
                return long.TryParse(text.AsSpan(start, _position - start), NumberStyles.None,
                    CultureInfo.InvariantCulture, out value);
            }

            if (!char.IsAsciiLetter(text[_position]) && text[_position] != '_') return false;

            var nameStart = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                _position++;
            var name = text[nameStart.._position];

            SkipWhitespace();
            if (_position < text.Length && text[_position] == '(')
                return TryParseCall(name, out value);

            return TryResolveSymbol(name, context, out value);
        }

        /// <summary>
        /// <c>min(a, b)</c> / <c>max(a, b)</c>, plus the quoted cross-reference forms
        /// (<c>skill('Fire Bolt'.blvl)</c>, <c>miss('firewall'.rang)</c>, …).
        /// </summary>
        private bool TryParseCall(string name, out long value)
        {
            value = 0;
            _position++; // consume '('
            SkipWhitespace();

            if (_position < text.Length && text[_position] == '\'')
                return TryParseReference(name, out value);

            if (!TryParseExpression(out var first)) return false;
            SkipWhitespace();
            if (!Match(',')) return false;
            if (!TryParseExpression(out var second)) return false;
            SkipWhitespace();
            if (!Match(')')) return false;

            if (name.Equals("min", StringComparison.OrdinalIgnoreCase))
            {
                value = Math.Min(first, second);
                return true;
            }
            if (name.Equals("max", StringComparison.OrdinalIgnoreCase))
            {
                value = Math.Max(first, second);
                return true;
            }
            return false;
        }

        /// <summary>
        /// A quoted reference such as <c>skill('Fire Bolt'.blvl)</c>. Only <c>skill(…)</c>
        /// resolves; the others (<c>miss</c>, <c>sklvl</c>, <c>stat</c>) reach into data the
        /// export does not carry, and the whole line is dropped instead of guessed at.
        /// </summary>
        private bool TryParseReference(string name, out long value)
        {
            value = 0;

            _position++; // consume the opening quote
            var start = _position;
            while (_position < text.Length && text[_position] != '\'') _position++;
            if (_position >= text.Length) return false;
            var target = text[start.._position];
            _position++; // consume the closing quote

            var fields = new List<string>();
            while (Match('.'))
            {
                var fieldStart = _position;
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                    _position++;
                fields.Add(text[fieldStart.._position]);
            }
            SkipWhitespace();
            if (!Match(')')) return false;

            // `stat('extra_skelewarriors'.accr)` sums a stat accumulated from the
            // character's gear. A skill preview has no gear, so the term is zero — the
            // same value the game shows a freshly-levelled character.
            if (name.Equals("stat", StringComparison.OrdinalIgnoreCase))
            {
                value = 0;
                return true;
            }

            if (!name.Equals("skill", StringComparison.OrdinalIgnoreCase)) return false;
            if (fields.Count != 1) return false;

            var field = fields[0];
            var isSelf = string.Equals(target, context.Skill.Skill, StringComparison.OrdinalIgnoreCase);

            if (isSelf) return TryResolveSymbol(field, context, out value);

            // Level-independent columns on another skill (e.g. Skeleton Mastery's Param1)
            // read straight through.
            if (context.Data.Skills.TryGetValue(target, out var referenced)
                && referenced.SourceRow is not null
                && (TryParseIndexed(field, "par", 1, 9, out var index)
                    || TryParseIndexed(field, "pa", 10, 20, out index)))
            {
                value = ParseLong(Param(referenced.SourceRow, index));
                return true;
            }

            // Everything else on another skill scales with *that* skill's level, and no
            // other skill is allocated in an isolated preview — so the term contributes
            // nothing, exactly as it would in game at rank 0.
            value = 0;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }

        private bool Match(char expected)
        {
            SkipWhitespace();
            if (_position >= text.Length || text[_position] != expected) return false;
            _position++;
            return true;
        }
    }
}
