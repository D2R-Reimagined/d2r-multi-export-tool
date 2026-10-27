// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// The two skill levels an evaluation is anchored to. <c>skillcalc.txt</c> is explicit that
/// these are different quantities — <c>lvl</c> is "the current Skill Level (with bonuses)"
/// and <c>blvl</c> is "the base Skill Level (without bonuses)" — and Reimagined uses the
/// distinction as a designed mechanic, paying soft points from +skills gear at a lower rate
/// than hard points (<c>Fire Mastery</c>'s
/// <c>blvl*par1 + min((lvl-blvl)*par2, par3)</c> is the canonical shape).
///
/// <see cref="UsedBaseLevel"/> records whether an evaluation actually read <c>blvl</c>, so
/// <see cref="SkillDescriptionImporter"/> can ship the cheap one-dimensional per-level table
/// for the ~95% of lines that do not care, and only pay for a hard/bonus grid on the ones
/// that do. <see cref="UsedCharacterLevel"/> does the same job for <c>ulvl</c>, which is a
/// third quantity again — the caster's own character level, independent of either skill
/// level. <see cref="UsedUnallocatedSkill"/> records that the evaluation reached another
/// skill's level-dependent quantity, which the isolated preview pins at zero points.
/// </summary>
internal sealed class CalcLevels(int level, int baseLevel, int characterLevel = 1)
{
    /// <summary>Effective skill level — hard points plus <c>+skills</c> bonuses.</summary>
    public int Level { get; } = level;

    /// <summary>Hard points allocated to the skill.</summary>
    public int BaseLevel { get; } = baseLevel;

    /// <summary>The caster's character level, as <c>skillcalc.txt</c>'s <c>ulvl</c>.</summary>
    public int CharacterLevel { get; } = characterLevel;

    /// <summary>True once <see cref="ReadBaseLevel"/> has been called on this instance.</summary>
    public bool UsedBaseLevel { get; private set; }

    /// <summary>True once <see cref="ReadCharacterLevel"/> has been called on this instance.</summary>
    public bool UsedCharacterLevel { get; private set; }

    /// <summary>True once <see cref="ReadUnallocatedSkill"/> has been called on this instance.</summary>
    public bool UsedUnallocatedSkill { get; private set; }

    /// <summary>Reads <see cref="BaseLevel"/>, flagging the evaluation as hard/soft sensitive.</summary>
    public int ReadBaseLevel()
    {
        UsedBaseLevel = true;
        return BaseLevel;
    }

    /// <summary>Reads <see cref="CharacterLevel"/>, flagging the evaluation as <c>ulvl</c>-sensitive.</summary>
    public int ReadCharacterLevel()
    {
        UsedCharacterLevel = true;
        return CharacterLevel;
    }

    /// <summary>
    /// Flags the evaluation as depending on another skill's live allocation, which the
    /// isolated preview fixes at zero points, so its solved value is only a baseline.
    /// </summary>
    public void ReadUnallocatedSkill() => UsedUnallocatedSkill = true;
}

/// <summary>
/// Why the current evaluation failed, and the chain of column/table expansions the
/// failure surfaced through.
///
/// Without it a dropped line could only be reported by its outermost expression, which is
/// routinely an alias that resolved perfectly well: <c>Thorns</c>' <c>ast1</c> is
/// <c>skillcalc.txt</c>'s name for <c>aurastatcalc1</c>, and that column reads
/// <c>edmn * (ulvl + 100)/100</c> — the blocker is <c>ulvl</c>, one level down. Only the
/// first (deepest) reason is kept; each enclosing expansion prepends itself as the stack
/// unwinds.
/// </summary>
internal sealed class CalcTrace
{
    /// <summary>The root cause of the current top-level evaluation, or null while it holds.</summary>
    public string? Reason { get; private set; }

    /// <summary>Optional observer of resolved intrinsic quantities, in their actual source scope.</summary>
    public Action<ICalcScope, string>? ObserveIntrinsic { get; init; }

    /// <summary>Clears the trace at the start of a top-level evaluation.</summary>
    public void Reset() => Reason = null;

    /// <summary>Records the deepest failure and returns false for the caller to propagate.</summary>
    public bool Fail(string reason)
    {
        Reason ??= reason;
        return false;
    }

    /// <summary>Notes the expansion a recorded failure surfaced through.</summary>
    public void Via(string frame)
    {
        if (Reason is not null) Reason = $"{frame} → {Reason}";
    }
}

/// <summary>
/// What an expression is evaluated against — a <c>skills.txt</c> row
/// (<see cref="SkillCalcContext"/>) or a <c>missiles.txt</c> row
/// (<see cref="MissileCalcContext"/>). Both dialects share one grammar and differ only in
/// their symbol vocabulary, so the parser talks to this instead of either row type.
/// </summary>
internal interface ICalcScope
{
    GameData Data { get; }

    /// <summary>The levels the evaluation is anchored to.</summary>
    CalcLevels Levels { get; }

    /// <summary>The <c>skillcalc.txt</c> / <c>misscalc.txt</c> vocabulary for this dialect.</summary>
    CalcCodeTable Codes { get; }

    /// <summary>Collects the root cause when an evaluation cannot be completed.</summary>
    CalcTrace Trace { get; }

    /// <summary>
    /// Symbols currently being expanded. Guards the self-referential calc chains D2R ships
    /// (a <c>Calc</c> column that mentions another <c>Calc</c> column that mentions the
    /// first) from recursing forever.
    /// </summary>
    HashSet<string> Expanding { get; }

    /// <summary>Resolves a quantity this dialect defines in code (<c>edmn</c>, <c>toht</c>, …).</summary>
    bool TryResolveIntrinsic(string symbol, out long value);

    /// <summary>Reads a source column named by a <c>The … field value</c> table row.</summary>
    bool TryResolveColumn(string column, out long value);

    /// <summary>
    /// Resolves a quoted cross-reference such as <c>skill('Fire Bolt'.blvl)</c> or
    /// <c>miss('firewall'.rang)</c>.
    /// </summary>
    bool TryResolveReference(string function, string target, List<string> fields, out long value);
}

/// <summary>
/// The calc-expression grammar shared by <c>skills.txt</c> and <c>missiles.txt</c>:
/// <c>ln12</c>, <c>min(1+(lvl-1)/2,14)</c>, <c>skill('Fire Bolt'.blvl)</c>,
/// <c>(a &gt; 0) ? b : c</c>.
///
/// The language is the game's own BBE ("Binary Bytecode Evaluator", per the Data Guide's
/// <c>bbe-calc.html</c>): the operators <c>+ - * / % ^</c>, the ternary <c>?:</c> and the
/// six comparisons, with <c>0</c> false and non-zero true, plus the functions
/// <c>min</c>/<c>max</c>/<c>rand</c> and the quoted references
/// <c>skill</c>/<c>sklvl</c>/<c>sksrc</c>/<c>miss</c>/<c>stat</c>/<c>cond</c>. A bare
/// identifier belongs to the scope of the file the cell lives in, which is why skills and
/// missiles need separate vocabularies.
///
/// The game evaluates these with 32-bit integer arithmetic (division truncates), so this
/// evaluator does too; the fractional part players see on mana costs and durations comes
/// from the description function's divisor column, applied afterwards by
/// <see cref="SkillDescriptionImporter"/>.
/// </summary>
internal static class CalcExpression
{
    internal enum ScanTokenKind
    {
        BareSymbol,
        Reference,
        Function,
        Unsupported
    }

    internal sealed record ScanToken(
        ScanTokenKind Kind,
        int Start,
        int Length,
        string Name,
        string? Target = null,
        List<string>? Fields = null);

    /// <summary>
    /// Evaluates <paramref name="expression"/> against <paramref name="scope"/>. Returns
    /// false (and <c>0</c>) for an empty expression, a syntax error, a division by zero, or
    /// any symbol the exporter cannot resolve — callers are expected to drop the line and
    /// report it rather than ship a guess.
    /// </summary>
    public static bool TryEvaluate(string? expression, ICalcScope scope, out long value)
    {
        scope.Trace.Reset();
        return TryEvaluateCore(expression, scope, out value);
    }

    /// <summary>
    /// Parses an expression without resolving any symbols and returns the normalized source
    /// plus the exact spans of its identifiers, functions and quoted references.
    /// Expressions outside the website evaluator's syntax are declined so callers can keep
    /// the original fallback intact.
    /// </summary>
    internal static bool TryScan(string? expression, ICalcScope scope, out string text, out List<ScanToken> tokens)
    {
        text = expression ?? "";
        tokens = [];
        scope.Trace.Reset();
        if (string.IsNullOrWhiteSpace(expression)) return scope.Trace.Fail("the expression is empty");

        var normalized = BalanceTerminalParentheses(expression.Trim().Trim('"'));
        if (normalized.Length == 0) return scope.Trace.Fail("the expression is empty");

        var scanned = new List<ScanToken>();
        var parser = new Parser(normalized, scope, tokens: scanned, evaluating: false);
        if (!parser.TryParseExpression(out _) || !parser.AtEnd)
            return scope.Trace.Fail($"could not parse '{normalized}'");
        if (scanned.Any(static token => token.Kind == ScanTokenKind.Unsupported))
            return scope.Trace.Fail($"'{normalized}' uses syntax the website evaluator does not support");

        text = normalized;
        tokens = scanned;
        return true;
    }

    /// <summary>Maps reference functions that are equivalent in the exported skill scope.</summary>
    internal static string CanonicalReferenceFunction(string function)
        => ReferenceAliases.TryGetValue(function, out var canonical) ? canonical : function;

    /// <summary>Evaluates a drop condition while checking references in both selected and unselected branches.</summary>
    internal static bool TryEvaluateWithReferenceValidation(string? expression, ICalcScope scope, out long value)
    {
        scope.Trace.Reset();
        return TryEvaluateCore(expression, scope, out value, inspectUnselectedReferences: true);
    }

    /// <summary>The body of <see cref="TryEvaluate"/>, without resetting the trace.</summary>
    private static bool TryEvaluateCore(string? expression, ICalcScope scope, out long value,
        Func<string, int, int, bool>? bareSymbol = null, bool inspectUnselectedReferences = false)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression)) return scope.Trace.Fail("the expression is empty");

        // Mod authors quote calc cells that contain a comma so the .txt column survives
        // a spreadsheet round-trip (e.g. "min(lvl*5,100)"); the quotes are not syntax.
        var text = BalanceTerminalParentheses(expression.Trim().Trim('"'));
        if (text.Length == 0) return scope.Trace.Fail("the expression is empty");

        var parser = new Parser(text, scope, bareSymbol, inspectUnselectedReferences);
        if (!parser.TryParseExpression(out var result))
            return scope.Trace.Fail($"could not parse '{text}'");
        if (!parser.AtEnd) return scope.Trace.Fail($"unexpected trailing input in '{text}'");

        value = result;
        return true;
    }

    /// <summary>
    /// Binds bare table-defined Param aliases to their source values using this parser.
    /// Quoted references stay dynamic. Other bare quantities cannot safely be evaluated
    /// in a different skill's client context, so such expressions are declined.
    /// </summary>
    public static bool TryBindParameters(string expression, SkillCalcContext scope, out string bound)
    {
        var text = BalanceTerminalParentheses(expression.Trim().Trim('"'));
        var replacements = new List<(int Start, int Length, long Value)>();
        scope.Trace.Reset();
        var success = TryEvaluateCore(text, scope, out _, (symbol, start, length) =>
        {
            if (!scope.Data.SkillCalcCodes.FieldAliases.TryGetValue(symbol, out var column)
                || !TryParseIndexed(column, "Param", 1, 20, out _)
                || !TryResolveSymbol(symbol, scope, out var value)) return false;
            replacements.Add((start, length, value));
            return true;
        });
        bound = text;
        if (!success) return false;
        foreach (var (start, length, value) in replacements.OrderByDescending(static r => r.Start))
            bound = bound.Remove(start, length).Insert(start, $"({value.ToString(CultureInfo.InvariantCulture)})");
        return true;
    }

    /// <summary>
    /// Evaluates a nested column or table formula, refusing to re-enter a symbol that is
    /// already being expanded.
    /// </summary>
    public static bool TryEvaluateNested(string symbol, string? expression, ICalcScope scope, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression))
            return scope.Trace.Fail($"'{symbol}' is empty on this row");
        if (!scope.Expanding.Add(symbol))
            return scope.Trace.Fail($"'{symbol}' expands to itself");
        try
        {
            if (TryEvaluateCore(expression, scope, out value)) return true;
            scope.Trace.Via($"{symbol} = '{expression.Trim()}'");
            return false;
        }
        finally
        {
            scope.Expanding.Remove(symbol);
        }
    }

    /// <summary>
    /// Resolves one bare symbol: first the quantities the dialect computes itself, then the
    /// column aliases and progression formulas declared by its reference table.
    /// </summary>
    public static bool TryResolveSymbol(string symbol, ICalcScope scope, out long value)
    {
        if (scope.TryResolveIntrinsic(symbol, out value))
        {
            scope.Trace.ObserveIntrinsic?.Invoke(scope, symbol);
            return true;
        }

        if (scope.Codes.FieldAliases.TryGetValue(symbol, out var column))
            return scope.TryResolveColumn(column, out value);

        if (scope.Codes.Formulas.TryGetValue(symbol, out var formula))
            return TryEvaluateNested(symbol, formula, scope, out value);

        value = 0;
        return scope.Codes.Prose.TryGetValue(symbol, out var prose)
            ? scope.Trace.Fail($"'{symbol}' is \"{prose}\", which this exporter does not model")
            : scope.Trace.Fail($"'{symbol}' is not a symbol the reference tables define");
    }

    /// <summary>
    /// Reimagined currently contains two Calc5 cells whose final <c>)</c> was lost during
    /// a spreadsheet round-trip. Their only unmatched delimiters are terminal opens, so
    /// closing them here preserves the otherwise-complete skillcalc expression. Extra
    /// closing parentheses remain invalid and are still rejected by the parser.
    /// </summary>
    private static string BalanceTerminalParentheses(string expression)
    {
        var depth = 0;
        var quoted = false;
        foreach (var character in expression)
        {
            if (character == '\'') quoted = !quoted;
            else if (!quoted && character == '(') depth++;
            else if (!quoted && character == ')') depth--;
            if (depth < 0) return expression;
        }
        return depth > 0 ? expression + new string(')', depth) : expression;
    }

    /// <summary>
    /// D2's five-bracket level progression: the base column plus one per-level column for
    /// levels 2–8, 9–16, 17–22, 23–28, and 29+. Shared by the physical (<c>MinDam</c>),
    /// elemental (<c>EMin</c>) and to-hit damage columns of both skills and missiles.
    /// </summary>
    public static long Progression(long baseValue, long l1, long l2, long l3, long l4, long l5, int level)
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
    public static long LengthProgression(long baseValue, long l1, long l2, long l3, int level)
        => baseValue
            + l1 * Math.Clamp(level - 1, 0, 7)
            + l2 * Math.Clamp(level - 8, 0, 8)
            + l3 * Math.Max(level - 16, 0);

    /// <summary>Keeps a shift column inside a range <c>&lt;&lt;</c> can act on.</summary>
    public static int ShiftBits(long raw) => (int)Math.Clamp(raw, 0, 32);

    public static long ParseLong(string? raw)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    public static long ParseLong(int? raw) => raw ?? 0;

    /// <summary>
    /// The "another skill, in isolation" policy, shared by every scope that can see a
    /// <c>skill('X'.…)</c> or <c>stat('…'.accr)</c> term.
    ///
    /// A level-independent column on the referenced skill (one whose code aliases a
    /// <c>Param</c> column — <c>skillcalc.txt</c> is what tells us which those are) reads
    /// straight through. A gear-accumulated <c>stat</c> contributes nothing, because an
    /// isolated preview wears no gear.
    ///
    /// Everything else is the last resort, reached only once
    /// <see cref="SkillCalcContext.TryResolveUnallocatedSkill"/> has declined to solve the
    /// referenced column against its own row — a column the mod leaves empty, or a
    /// reference that cycles. The term then contributes nothing, which is what a skill with
    /// no points allocated is worth. Returns false only for a shape that is not a
    /// skill/stat reference at all.
    /// </summary>
    public static bool TryResolveIsolatedSkill(
        GameData data,
        string function,
        string target,
        string? field,
        out long value)
    {
        value = 0;

        if (function.Equals("stat", StringComparison.OrdinalIgnoreCase)) return true;
        if (!function.Equals("skill", StringComparison.OrdinalIgnoreCase)) return false;
        if (field is null) return false;

        if (data.Skills.TryGetValue(target, out var referenced)
            && referenced.SourceRow is not null
            && data.SkillCalcCodes.FieldAliases.TryGetValue(field, out var column)
            && TryParseIndexed(column, "Param", 1, 20, out var index))
        {
            value = ParseLong(SkillCalcSource.Param(referenced.SourceRow, index));
        }

        return true;
    }

    /// <summary>
    /// The quoted cross-reference functions BBE defines. The two reference tables list the
    /// identifiers each scope accepts but not the functions themselves, so this is the Data
    /// Guide's list; it exists only to tell a genuine typo apart from a real reference, so
    /// the import report can say which.
    /// </summary>
    private static readonly HashSet<string> ReferenceFunctions =
        new(StringComparer.OrdinalIgnoreCase) { "skill", "sklvl", "sksrc", "stat", "miss", "cond" };

    /// <summary>
    /// Reference functions that differ from another only in which unit they read, where the
    /// distinction cannot arise in the scopes this exporter evaluates, mapped onto the
    /// function they are equivalent to.
    ///
    /// <c>sksrc</c> ("skill source") reads a skill on the <em>source</em> unit. The Data
    /// Guide is explicit that for skill, missile, item and treasure-class scopes the source
    /// unit is the same one <c>skill</c> itself uses, and only monster-scope BBE — which
    /// this exporter never evaluates — is passed a different unit. So the two are the same
    /// function here, not an approximation. Reimagined uses it in <c>Abyss</c>'
    /// <c>dsc2calca1</c> and in <c>abysscenter</c>/<c>abyssexplode</c>'s <c>Radius</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ReferenceAliases =
        new(StringComparer.OrdinalIgnoreCase) { ["sksrc"] = "skill" };

    /// <summary>
    /// The conditions <c>cond</c> accepts, per the Data Guide. Every one but
    /// <c>Difficulty</c> interrogates a live unit — its type, class, item quality, elite
    /// rank, or whether its level is desecrated — which a static export has no stand-in
    /// for. Listed so a scope can report "a condition we cannot evaluate" rather than
    /// "not a condition", which is what a misspelling deserves.
    /// </summary>
    public static readonly IReadOnlySet<string> Conditions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IsType", "IsClass", "Desecrated", "Difficulty",
            "MonsterTestElite", "ItemIsType", "ItemIsModType", "MonsterHasMod"
        };

    /// <summary>
    /// Resolves <c>cond('Difficulty', hell)</c>, the one condition a static export can
    /// answer. The export shows a single difficulty, named by
    /// <see cref="Config.ExportConfig.SkillDescriptionDifficulty"/>, so the matching branch
    /// is 1 and the other two are 0 — exactly what the game substitutes on that
    /// difficulty. Shared by both scopes because the Data Guide specifies that a missile's
    /// conditions are evaluated against its source unit, which is the caster, so the answer
    /// cannot differ from the skill's own.
    /// </summary>
    public static bool TryResolveCondition(
        GameData data,
        string target,
        List<string> fields,
        CalcTrace trace,
        out long value)
    {
        value = 0;

        if (target.Equals("Difficulty", StringComparison.OrdinalIgnoreCase) && fields.Count == 1)
        {
            value = fields[0].Equals(data.ExportConfig.SkillDescriptionDifficulty, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            return true;
        }

        return Conditions.Contains(target)
            ? trace.Fail($"cond('{target}', …) asks about a live unit, which the export does not model")
            : trace.Fail($"cond('{target}', …) is not a condition BBE defines");
    }

    /// <summary>Matches <c>prefix</c> followed by a single index inside the given bounds.</summary>
    public static bool TryParseIndexed(string symbol, string prefix, int min, int max, out int index)
    {
        index = 0;
        if (!symbol.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var digits = symbol[prefix.Length..];
        if (digits.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out index))
            return false;
        return index >= min && index <= max;
    }

    /// <summary>
    /// Recursive-descent parser over one calc expression. Precedence, loosest first:
    /// ternary <c>?:</c>, comparison, additive, multiplicative, unary minus, power,
    /// primary.
    /// </summary>
    private sealed class Parser(string text, ICalcScope scope, Func<string, int, int, bool>? bareSymbol = null,
        bool inspectUnselectedReferences = false, List<ScanToken>? tokens = null, bool evaluating = true)
    {
        private int _position;
        private bool _evaluate = evaluating;

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
            if (!TryParseBranch(_evaluate && condition != 0, out var whenTrue)) return false;
            SkipWhitespace();
            if (!Match(':')) return false;
            if (!TryParseBranch(_evaluate && condition == 0, out var whenFalse)) return false;

            value = condition != 0 ? whenTrue : whenFalse;
            return true;
        }

        private bool TryParseBranch(bool evaluate, out long value)
        {
            var previous = _evaluate;
            _evaluate = evaluate;
            try
            {
                return TryParseExpression(out value);
            }
            finally
            {
                _evaluate = previous;
            }
        }

        private bool TryParseComparison(out long value)
        {
            if (!TryParseAdditive(out value)) return false;

            SkipWhitespace();
            var op = PeekComparisonOperator();
            if (op is null) return true;

            if (op == "=") tokens?.Add(new ScanToken(ScanTokenKind.Unsupported, _position, op.Length, op));
            _position += op.Length;
            if (!TryParseAdditive(out var right))
            {
                value = 0;
                return false;
            }

            if (!_evaluate) return true;
            value = op switch
            {
                ">" => value > right ? 1 : 0,
                "<" => value < right ? 1 : 0,
                ">=" => value >= right ? 1 : 0,
                "<=" => value <= right ? 1 : 0,
                "=" or "==" => value == right ? 1 : 0,
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
            if (remaining[0] == '=') return "=";
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
                if (_evaluate)
                {
                    value = op == '+' ? value + right : value - right;
                }
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
                if (op != '*' && op != '/' && op != '%') return true;

                if (op == '%') tokens?.Add(new ScanToken(ScanTokenKind.Unsupported, _position, 1, "%"));
                _position++;
                if (!TryParseUnary(out var right))
                {
                    value = 0;
                    return false;
                }
                if (!_evaluate) continue;
                if (op is '/' or '%')
                {
                    if (right == 0)
                    {
                        value = 0;
                        return false;
                    }
                    value = op == '/' ? value / right : value % right;
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
                if (_evaluate)
                {
                    value = -value;
                }
                return true;
            }
            if (Match('+')) return TryParseUnary(out value);
            return TryParsePower(out value);
        }

        /// <summary>
        /// <c>a^b</c>, binding tighter than unary minus (so <c>-2^2</c> is <c>-4</c>) and
        /// right-associative. Integer like the rest of the evaluator; a negative exponent
        /// would truncate to zero in every case, so it is rejected rather than rounded.
        /// </summary>
        private bool TryParsePower(out long value)
        {
            if (!TryParsePrimary(out value)) return false;

            SkipWhitespace();
            if (_position < text.Length && text[_position] == '^')
                tokens?.Add(new ScanToken(ScanTokenKind.Unsupported, _position, 1, "^"));
            if (!Match('^')) return true;

            if (!TryParseUnary(out var exponent))
            {
                value = 0;
                return false;
            }
            if (!_evaluate) return true;
            if (exponent is < 0 or > 32)
            {
                value = 0;
                return scope.Trace.Fail($"an exponent of {exponent} is outside what a calc cell can mean");
            }

            var result = 1L;
            for (var i = 0; i < exponent; i++) result *= value;
            value = result;
            return true;
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

            if (char.IsAsciiDigit(text[_position])) return TryParseNumber(out value);

            if (!char.IsAsciiLetter(text[_position]) && text[_position] != '_') return false;

            var nameStart = _position;
            while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                _position++;
            var name = text[nameStart.._position];
            var nameLength = _position - nameStart;

            SkipWhitespace();
            if (_position < text.Length && text[_position] == '(')
                return TryParseCall(name, nameStart, out value);

            tokens?.Add(new ScanToken(ScanTokenKind.BareSymbol, nameStart, nameLength, name));
            if (bareSymbol is not null && !bareSymbol(name, nameStart, nameLength)) return false;
            return !_evaluate && bareSymbol is null && !inspectUnselectedReferences
                || TryResolveSymbol(name, scope, out value);
        }

        /// <summary>
        /// Reads an integer literal, tolerating the fractional spelling a spreadsheet
        /// round-trip can leave behind (Reimagined ships exactly one, Energy Shield's
        /// <c>-6.25</c>). The evaluator is integer by design, so a fraction is rounded
        /// rather than carried — still far closer than dropping the line.
        /// </summary>
        private bool TryParseNumber(out long value)
        {
            var start = _position;
            while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;

            var isFraction = _position + 1 < text.Length
                && text[_position] == '.'
                && char.IsAsciiDigit(text[_position + 1]);
            if (!isFraction)
            {
                return long.TryParse(text.AsSpan(start, _position - start), NumberStyles.None,
                    CultureInfo.InvariantCulture, out value);
            }

            _position++;
            while (_position < text.Length && char.IsAsciiDigit(text[_position])) _position++;
            tokens?.Add(new ScanToken(ScanTokenKind.Unsupported, start, _position - start,
                text[start.._position]));
            if (!double.TryParse(text.AsSpan(start, _position - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                value = 0;
                return false;
            }
            value = (long)Math.Round(parsed, MidpointRounding.AwayFromZero);
            return true;
        }

        /// <summary>
        /// <c>min(a, b)</c> / <c>max(a, b)</c>, plus the quoted cross-reference forms
        /// (<c>skill('Fire Bolt'.blvl)</c>, <c>miss('firewall'.rang)</c>, …).
        /// </summary>
        private bool TryParseCall(string name, int callStart, out long value)
        {
            value = 0;
            _position++; // consume '('
            SkipWhitespace();

            if (_position < text.Length && text[_position] == '\'')
                return TryParseReference(name, callStart, out value);

            if (!TryParseExpression(out var first)) return false;
            SkipWhitespace();
            if (!Match(',')) return false;
            if (!TryParseExpression(out var second)) return false;
            SkipWhitespace();
            if (!Match(')')) return false;

            tokens?.Add(new ScanToken(ScanTokenKind.Function, callStart, _position - callStart, name));

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
            // `rand(a,b)` is rolled per cast, so there is no single number to export. The
            // mod uses it once, on a monster skill's calc1, which no skilldesc line reads.
            if (name.Equals("rand", StringComparison.OrdinalIgnoreCase))
                return !_evaluate || scope.Trace.Fail($"rand({first},{second}) is rolled at run time, so it has no exported value");
            return scope.Trace.Fail($"'{name}(…)' is not a calc function");
        }

        /// <summary>
        /// A quoted reference such as <c>skill('Fire Bolt'.blvl)</c>. The scope decides
        /// which functions it understands.
        /// </summary>
        private bool TryParseReference(string name, int callStart, out long value)
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
                if (_position == fieldStart) return false;
                fields.Add(text[fieldStart.._position]);
            }

            // `cond('Difficulty', hell)` separates its second argument with a comma and
            // leaves it unquoted; every other reference form uses dotted fields. Both end
            // up in the same list, so the scope sees one shape.
            while (Match(','))
            {
                SkipWhitespace();
                var argStart = _position;
                while (_position < text.Length && (char.IsAsciiLetterOrDigit(text[_position]) || text[_position] == '_'))
                    _position++;
                if (_position == argStart) return false;
                fields.Add(text[argStart.._position]);
            }

            SkipWhitespace();
            var reference = $"{name}('{target}'{string.Concat(fields.Select(field => "." + field))})";
            if (ReferenceAliases.TryGetValue(name, out var canonical)) name = canonical;
            if (!ReferenceFunctions.Contains(name))
                return scope.Trace.Fail($"'{name}(…)' is not a function skillcalc.txt or misscalc.txt defines");
            if (!Match(')')) return scope.Trace.Fail($"unsupported argument list in {reference}");
            tokens?.Add(new ScanToken(ScanTokenKind.Reference, callStart, _position - callStart,
                name, target, fields));

            if (!_evaluate && bareSymbol is null && !inspectUnselectedReferences) return true;
            if (scope.TryResolveReference(name, target, fields, out value)) return true;
            return scope.Trace.Fail($"{reference} could not be resolved");
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
