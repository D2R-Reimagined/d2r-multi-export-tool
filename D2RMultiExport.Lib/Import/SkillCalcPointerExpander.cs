// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using D2RMultiExport.Lib.Exporters;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Rewrites allocation-sensitive skill description expressions using the source columns
/// the website does not carry. Every replacement is parsed from the existing skillcalc
/// grammar and is all-or-nothing, so an unsupported dependency keeps its original fallback.
/// </summary>
internal static class SkillCalcPointerExpander
{
    public static string Expand(string expression, SkillEntry describedSkill, GameData data)
    {
        if (string.IsNullOrWhiteSpace(expression)
            || describedSkill.SourceRow is null
            || !SkillTreeExporter.IsExported(describedSkill, data))
        {
            return expression;
        }

        return TryExpandExpression(expression, describedSkill, describedSkill, data,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), out var expanded)
            ? expanded
            : expression;
    }

    private static bool TryExpandExpression(
        string expression,
        SkillEntry scopeSkill,
        SkillEntry describedSkill,
        GameData data,
        HashSet<string> expanding,
        out string expanded)
    {
        expanded = expression;
        if (scopeSkill.SourceRow is null) return false;

        var context = new SkillCalcContext
        {
            Skill = scopeSkill,
            Data = data,
            Levels = new CalcLevels(1, 1)
        };
        if (!CalcExpression.TryScan(expression, context, out var text, out var tokens)) return false;

        var replacements = new List<(int Start, int Length, string Value)>();
        foreach (var token in tokens)
        {
            string? replacement;
            switch (token.Kind)
            {
                case CalcExpression.ScanTokenKind.BareSymbol:
                    if (!TryExpandSymbol(token.Name, scopeSkill, describedSkill, data, expanding,
                            false, out replacement)) return false;
                    break;

                case CalcExpression.ScanTokenKind.Reference:
                    if (!TryExpandReference(token, scopeSkill, describedSkill, data, expanding,
                            out replacement)) return false;
                    break;

                case CalcExpression.ScanTokenKind.Function:
                    if (token.Name.Equals("rand", StringComparison.OrdinalIgnoreCase)) return false;
                    continue;

                default:
                    return false;
            }

            if (replacement is not null
                && !text.AsSpan(token.Start, token.Length).SequenceEqual(replacement.AsSpan()))
            {
                replacements.Add((token.Start, token.Length, $"({replacement})"));
            }
        }

        foreach (var (start, length, value) in replacements.OrderByDescending(static item => item.Start))
            text = text.Remove(start, length).Insert(start, value);
        expanded = replacements.Count == 0 ? expression : text;
        return true;
    }

    private static bool TryExpandSymbol(
        string symbol,
        SkillEntry scopeSkill,
        SkillEntry describedSkill,
        GameData data,
        HashSet<string> expanding,
        bool referencedField,
        out string? replacement)
    {
        replacement = null;
        var sameAsDescription = SameSkill(scopeSkill, describedSkill);

        if (IsLevelSymbol(symbol))
        {
            if (sameAsDescription) replacement = referencedField ? symbol : null;
            else
            {
                if (!SkillTreeExporter.IsExported(scopeSkill, data)) return false;
                replacement = SkillReference(scopeSkill, symbol.Equals("sklvl", StringComparison.OrdinalIgnoreCase)
                    ? "lvl" : symbol);
            }
            return true;
        }

        if (symbol.Equals("ulvl", StringComparison.OrdinalIgnoreCase))
        {
            replacement = referencedField ? symbol : null;
            return true;
        }

        if (data.SkillCalcCodes.Formulas.ContainsKey(symbol))
        {
            if (sameAsDescription) replacement = referencedField ? symbol : null;
            else
            {
                if (!SkillTreeExporter.IsExported(scopeSkill, data)) return false;
                replacement = SkillReference(scopeSkill, symbol);
            }
            return true;
        }

        if (!data.SkillCalcCodes.FieldAliases.TryGetValue(symbol, out var column)) return false;

        if (CalcExpression.TryParseIndexed(column, "Param", 1, 20, out var paramIndex))
        {
            if (sameAsDescription)
            {
                replacement = referencedField ? symbol : null;
                return true;
            }
            if (scopeSkill.SourceRow is null) return false;
            replacement = CalcExpression.ParseLong(SkillCalcSource.Param(scopeSkill.SourceRow, paramIndex))
                .ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (CalcExpression.TryParseIndexed(column, "Calc", 1, 10, out _))
        {
            if (CellNeedsExpansion(scopeSkill, column, describedSkill, data, []))
            {
                if (!TryExpandCell(scopeSkill, column, describedSkill, data, expanding, out var cell)) return false;
                replacement = cell;
                return true;
            }

            if (sameAsDescription) replacement = referencedField ? symbol : null;
            else
            {
                if (!SkillTreeExporter.IsExported(scopeSkill, data)) return false;
                replacement = SkillReference(scopeSkill, symbol);
            }
            return true;
        }

        if (scopeSkill.SourceRow is null
            || SkillCalcContext.ExpressionColumn(scopeSkill.SourceRow, column) is null)
        {
            return false;
        }

        if (!TryExpandCell(scopeSkill, column, describedSkill, data, expanding, out var expandedCell)) return false;
        replacement = expandedCell;
        return true;
    }

    private static bool TryExpandReference(
        CalcExpression.ScanToken token,
        SkillEntry scopeSkill,
        SkillEntry describedSkill,
        GameData data,
        HashSet<string> expanding,
        out string? replacement)
    {
        replacement = null;
        var fields = token.Fields ?? [];

        if (token.Name.Equals("stat", StringComparison.OrdinalIgnoreCase))
            return fields.Count == 1;

        if (token.Name.Equals("cond", StringComparison.OrdinalIgnoreCase))
        {
            var trace = new CalcTrace();
            if (!CalcExpression.TryResolveCondition(data, token.Target ?? "", fields, trace, out var value))
                return false;
            replacement = value.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (token.Name.Equals("sklvl", StringComparison.OrdinalIgnoreCase))
        {
            if (fields.Count == 0 || token.Target is null) return false;
            if (!data.Skills.TryGetValue(token.Target, out var referenced)
                || referenced.SourceRow is null) return false;

            if (!SameSkill(referenced, describedSkill))
            {
                if (!data.SkillCalcCodes.FieldAliases.TryGetValue(fields[^1], out var column)
                    || !CalcExpression.TryParseIndexed(column, "Param", 1, 20, out var index)) return false;
                replacement = CalcExpression.ParseLong(SkillCalcSource.Param(referenced.SourceRow, index))
                    .ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (!TryExpandSymbol(fields[^1], describedSkill, describedSkill, data, expanding,
                    true, out replacement)) return false;
            return true;
        }

        if (!token.Name.Equals("skill", StringComparison.OrdinalIgnoreCase)
            || fields.Count != 1
            || token.Target is null
            || !data.Skills.TryGetValue(token.Target, out var target)
            || target.SourceRow is null)
        {
            return false;
        }

        return TryExpandSymbol(fields[0], target, describedSkill, data, expanding, true, out replacement);
    }

    private static bool TryExpandCell(
        SkillEntry skill,
        string column,
        SkillEntry describedSkill,
        GameData data,
        HashSet<string> expanding,
        out string expanded)
    {
        expanded = "";
        if (skill.SourceRow is null) return false;
        var expression = SkillCalcContext.ExpressionColumn(skill.SourceRow, column);
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var key = $"{skill.Id}:{column}";
        if (!expanding.Add(key)) return false;
        try
        {
            return TryExpandExpression(expression, skill, describedSkill, data, expanding, out expanded);
        }
        finally
        {
            expanding.Remove(key);
        }
    }

    private static bool CellNeedsExpansion(
        SkillEntry skill,
        string column,
        SkillEntry describedSkill,
        GameData data,
        HashSet<string> visiting)
    {
        if (skill.SourceRow is null) return false;
        var expression = SkillCalcContext.ExpressionColumn(skill.SourceRow, column);
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var key = $"{skill.Id}:{column}";
        if (!visiting.Add(key)) return true;
        try
        {
            var context = new SkillCalcContext
            {
                Skill = skill,
                Data = data,
                Levels = new CalcLevels(1, 1)
            };
            if (!CalcExpression.TryScan(expression, context, out _, out var tokens)) return false;
            return tokens.Any(token => TokenNeedsExpansion(token, skill, describedSkill, data, visiting));
        }
        finally
        {
            visiting.Remove(key);
        }
    }

    private static bool TokenNeedsExpansion(
        CalcExpression.ScanToken token,
        SkillEntry scopeSkill,
        SkillEntry describedSkill,
        GameData data,
        HashSet<string> visiting)
    {
        if (token.Kind == CalcExpression.ScanTokenKind.BareSymbol)
        {
            if (!data.SkillCalcCodes.FieldAliases.TryGetValue(token.Name, out var column)) return false;
            if (CalcExpression.TryParseIndexed(column, "Param", 1, 20, out _)) return false;
            if (CalcExpression.TryParseIndexed(column, "Calc", 1, 10, out _))
                return CellNeedsExpansion(scopeSkill, column, describedSkill, data, visiting);
            return scopeSkill.SourceRow is not null
                && SkillCalcContext.ExpressionColumn(scopeSkill.SourceRow, column) is not null;
        }

        if (token.Kind != CalcExpression.ScanTokenKind.Reference) return false;
        if (token.Name.Equals("cond", StringComparison.OrdinalIgnoreCase))
            return token.Target?.Equals("Difficulty", StringComparison.OrdinalIgnoreCase) == true;
        if (!token.Name.Equals("skill", StringComparison.OrdinalIgnoreCase)
            || token.Fields is not { Count: 1 }
            || token.Target is null
            || !data.Skills.TryGetValue(token.Target, out var target)
            || target.SourceRow is null
            || !data.SkillCalcCodes.FieldAliases.TryGetValue(token.Fields[0], out var targetColumn)) return false;

        if (CalcExpression.TryParseIndexed(targetColumn, "Param", 1, 20, out _)) return false;
        if (CalcExpression.TryParseIndexed(targetColumn, "Calc", 1, 10, out _))
            return CellNeedsExpansion(target, targetColumn, describedSkill, data, visiting);
        return SkillCalcContext.ExpressionColumn(target.SourceRow, targetColumn) is not null;
    }

    private static bool IsLevelSymbol(string symbol)
        => symbol.Equals("lvl", StringComparison.OrdinalIgnoreCase)
           || symbol.Equals("sklvl", StringComparison.OrdinalIgnoreCase)
           || symbol.Equals("blvl", StringComparison.OrdinalIgnoreCase);

    private static bool SameSkill(SkillEntry left, SkillEntry right)
        => left.Id == right.Id
           || left.Skill.Equals(right.Skill, StringComparison.OrdinalIgnoreCase);

    private static string SkillReference(SkillEntry skill, string field)
        => $"skill('{skill.Skill}'.{field})";
}