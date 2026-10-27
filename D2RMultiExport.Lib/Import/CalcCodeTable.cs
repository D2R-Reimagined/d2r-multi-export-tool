// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// The symbol vocabulary a calc expression may use, read from the game's own
/// <c>skillcalc.txt</c> / <c>misscalc.txt</c> reference tables rather than hardcoded.
///
/// Both files are two columns — <c>code</c> and a <c>*Description</c> comment — and the
/// comment column is written in three recognisable shapes:
///
/// <list type="bullet">
/// <item><description>a literal formula over other codes
/// (<c>ln12</c> → <c>par1 + (lvl - 1) * par2</c>), captured into <see cref="Formulas"/>
/// and evaluated by the same parser that handles the source cell;</description></item>
/// <item><description>a pointer at a source column
/// (<c>clc0</c> → <c>The calc10 field value</c>), captured into <see cref="FieldAliases"/>
/// as the bare column token (<c>calc10</c>);</description></item>
/// <item><description>prose naming a derived game quantity
/// (<c>edmn</c> → <c>Elemental Damage Min</c>), which has no mechanical definition in the
/// table and stays the calculators' responsibility.</description></item>
/// </list>
///
/// Reading the table instead of transcribing it matters because the spellings are not
/// guessable: <c>clc0</c> is <c>calc10</c> (not <c>calc0</c>), <c>ps10</c>–<c>ps14</c>
/// break the <c>pstN</c> pattern, and <c>ln91</c>/<c>ln21</c>/<c>ln43</c>/<c>ln65</c>/
/// <c>ln87</c>/<c>ln92</c> address Param9–Param20 rather than the digits in their names.
/// </summary>
public sealed class CalcCodeTable
{
    private static readonly Regex FieldAliasPattern = new(
        @"^The\s+(?:client\s+|server\s+)?(?<column>[A-Za-z][A-Za-z0-9_]*)\s+field\s+value$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private CalcCodeTable(
        Dictionary<string, string> formulas,
        Dictionary<string, string> fieldAliases,
        Dictionary<string, string> prose)
    {
        Formulas = formulas;
        FieldAliases = fieldAliases;
        Prose = prose;
    }

    /// <summary>Codes whose description column is itself an evaluable expression.</summary>
    public IReadOnlyDictionary<string, string> Formulas { get; }

    /// <summary>Codes that alias a single source column, keyed to the bare column token.</summary>
    public IReadOnlyDictionary<string, string> FieldAliases { get; }

    /// <summary>
    /// Codes the table names in prose rather than defining (<c>edmn</c> → "Elemental Damage
    /// Min"). Resolving them is the calculators' job; the text is kept so a dropped line can
    /// say which documented quantity it needed instead of claiming the symbol is unknown.
    /// </summary>
    public IReadOnlyDictionary<string, string> Prose { get; }

    /// <summary>A table with no entries, used when the reference file cannot be found.</summary>
    public static CalcCodeTable Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a reference table. <paramref name="formulaOverrides"/> replaces the formula a
    /// row ships with, and only for codes the table already defines as formulas, so an
    /// override documents a deviation from the printed text rather than inventing
    /// vocabulary. See <c>export-config.json</c> → <c>calcFormulaOverrides</c>.
    /// </summary>
    public static async Task<CalcCodeTable> LoadAsync(
        string path,
        IReadOnlyDictionary<string, string>? formulaOverrides = null)
    {
        var formulas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prose = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var lines = await File.ReadAllLinesAsync(path);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var columns = line.Split('\t');
            if (columns.Length < 2) continue;

            var code = columns[0].Trim();
            var description = columns[1].Trim();
            if (code.Length == 0 || description.Length == 0) continue;

            var alias = FieldAliasPattern.Match(description);
            if (alias.Success)
            {
                aliases[code] = alias.Groups["column"].Value;
                continue;
            }

            // Prose descriptions never carry an operator; every formula row does.
            if (description.AsSpan().IndexOfAny('+', '*', '/') >= 0) formulas[code] = description;
            else prose[code] = description;
        }

        if (formulaOverrides is not null)
        {
            foreach (var (code, formula) in formulaOverrides)
            {
                if (formulas.ContainsKey(code) && !string.IsNullOrWhiteSpace(formula))
                    formulas[code] = formula;
            }
        }

        return new CalcCodeTable(formulas, aliases, prose);
    }
}
