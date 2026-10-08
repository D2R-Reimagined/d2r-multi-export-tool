// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Checks the BBE drop conditions of <c>treasureclassex.txt</c> (<c>ConditionCalc</c>) and
/// <c>uniqueitems.txt</c> / <c>setitems.txt</c> (<c>DropConditionCalc</c>) with the same
/// grammar the skill exporter evaluates — <see cref="CalcExpression"/> — instead of a second
/// parser.
///
/// A condition is a <em>treasure-class scope</em> expression and almost always interrogates
/// the live game (<c>stat('heraldtier'.accr)</c>, <c>cond('Desecrated', …)</c>), so the
/// exporter does not evaluate it: the raw text is exported unchanged and the website decides
/// per source. What can be decided statically is whether the cell is well formed — balanced,
/// using only BBE operators and functions, naming a condition BBE defines, and referencing a
/// stat / skill / missile row the mod actually has. A cell that fails is a mod data error the
/// website could only silently misread, so it is reported with its row.
///
/// Every reference resolves to <c>1</c> during the check; the value is discarded. That keeps
/// division and comparison well defined without inventing a game state.
/// </summary>
internal static class DropConditionValidator
{
    /// <summary>
    /// Returns true for an empty or well-formed condition; otherwise false with the root
    /// cause from <see cref="CalcTrace"/> in <paramref name="reason"/>.
    /// </summary>
    public static bool TryValidate(string? expression, GameData data, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(expression)) return true;

        var scope = new ValidationScope(data);
        if (CalcExpression.TryEvaluateWithReferenceValidation(expression, scope, out _)) return true;
        reason = scope.Trace.Reason ?? $"could not parse '{expression.Trim()}'";
        return false;
    }

    private static readonly string[] Difficulties = ["normal", "nightmare", "hell"];

    /// <summary>
    /// Statically resolves a condition that depends on nothing but the difficulty — the one
    /// BBE condition a static export can answer (see <c>AGENTS.md</c> → <i>Difficulty is fixed,
    /// not dropped</i>). The same grammar is evaluated once per difficulty with
    /// <c>cond('Difficulty', x)</c> = 1 on difficulty <c>x</c> and 0 elsewhere; the result lists
    /// the difficulty indexes (0 normal, 1 nightmare, 2 hell) on which the condition holds.
    /// Returns false — and the raw text remains the only truth — when the condition is empty,
    /// malformed, or reads anything a live game supplies (stats, other conditions, skills).
    /// </summary>
    public static bool TryResolveDifficulties(string? expression, GameData data, out int[] difficulties)
    {
        difficulties = [];
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var result = new List<int>(Difficulties.Length);
        for (var difficulty = 0; difficulty < Difficulties.Length; difficulty++)
        {
            var scope = new ValidationScope(data, difficulty);
            if (!CalcExpression.TryEvaluateWithReferenceValidation(expression, scope, out var value)) return false;
            if (value != 0) result.Add(difficulty);
        }
        difficulties = [.. result];
        return true;
    }

    /// <param name="data">Rows that references are checked against.</param>
    /// <param name="difficulty">
    /// <c>null</c> to validate (every reference resolves to 1); a difficulty index to resolve
    /// statically, in which case any reference other than <c>cond('Difficulty', …)</c> fails
    /// because its value only exists in a running game.
    /// </param>
    private sealed class ValidationScope(GameData data, int? difficulty = null) : ICalcScope
    {
        public GameData Data { get; } = data;
        public CalcLevels Levels { get; } = new(1, 1);
        public CalcCodeTable Codes => CalcCodeTable.Empty;
        public CalcTrace Trace { get; } = new();
        public HashSet<string> Expanding { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool TryResolveIntrinsic(string symbol, out long value)
        {
            value = 0;
            return Trace.Fail($"'{symbol}' is a bare identifier, which a treasure-class condition does not define");
        }

        public bool TryResolveColumn(string column, out long value)
        {
            value = 0;
            return Trace.Fail($"'{column}' is not a column a treasure-class condition can read");
        }

        public bool TryResolveReference(string function, string target, List<string> fields, out long value)
        {
            value = 1;
            switch (function.ToLowerInvariant())
            {
                case "cond":
                    if (target.Equals("Difficulty", StringComparison.OrdinalIgnoreCase))
                    {
                        var index = fields.Count == 1
                            ? Array.FindIndex(Difficulties, d => d.Equals(fields[0], StringComparison.OrdinalIgnoreCase))
                            : -1;
                        if (index < 0)
                            return Trace.Fail($"cond('Difficulty', …) expects one of {string.Join("/", Difficulties)}");
                        if (difficulty is { } current) value = index == current ? 1 : 0;
                        return true;
                    }
                    if (difficulty is not null)
                        return Trace.Fail($"cond('{target}', …) asks about a live game");
                    return CalcExpression.Conditions.Contains(target)
                        || Trace.Fail($"cond('{target}', …) is not a condition BBE defines");
                case "stat" when difficulty is not null:
                case "skill" when difficulty is not null:
                case "sklvl" when difficulty is not null:
                case "miss" when difficulty is not null:
                    value = 0;
                    return Trace.Fail($"{function}('{target}') is only known in a running game");
                case "stat":
                    return Data.Stats.ContainsKey(target)
                        || Trace.Fail($"stat('{target}') names no itemstatcost.txt row");
                case "skill":
                case "sklvl":
                    return Data.Skills.ContainsKey(target)
                        || Trace.Fail($"{function}('{target}') names no skills.txt row");
                case "miss":
                    return Data.Missiles.ContainsKey(target)
                        || Trace.Fail($"miss('{target}') names no missiles.txt row");
                default:
                    value = 0;
                    return false;
            }
        }
    }
}
