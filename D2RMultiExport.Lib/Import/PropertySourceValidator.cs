// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.ErrorHandling;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// One item property slot exactly as the typed parser DTO exposed it — before code expansion,
/// parameter normalization, property-group expansion or cleanup — together with the source
/// column names used to report it (e.g. <c>prop6</c>/<c>par6</c>/<c>min6</c>/<c>max6</c>).
/// A blank or non-numeric Min/Max cell arrives as <c>null</c>; an explicit <c>0</c> stays 0.
/// </summary>
public readonly record struct SourcePropertySlot(
    string CodeColumn,
    string ParamColumn,
    string MinColumn,
    string MaxColumn,
    string? Code,
    string? Parameter,
    int? Min,
    int? Max);

/// <summary>
/// The shared check of raw item property slots against the <c>properties.txt</c> function that
/// consumes their param/min/max cells. Every item importer (uniques, set items, set bonuses,
/// runewords, gems/runes, cube outputs, magic affixes, automagic) reports through it, so a rule
/// is defined once. Findings are import errors naming the file, item, slot, code and values; the
/// slot is still exported as written — nothing is corrected, filled in or dropped here.
/// </summary>
public static class PropertySourceValidator
{
    /// <summary>Import-report category every finding is filed under.</summary>
    public const string Category = "SourceProperties";

    /// <summary>
    /// <c>properties.txt</c> functions whose first applied slot reads Min/Max as a value range
    /// (CASC Data Guide <c>properties.html</c>, <c>func#</c>): regular/reused rolls (1, 3, 8),
    /// damage (5, 6, 7, 15+16), single skill/tab levels (9, 10), durability (13), by-time
    /// (18) and the parameterized rolls (21, 22, 24, 36). Engine behavior, not a tunable list.
    /// </summary>
    private static readonly HashSet<int> RangeFunctions = [1, 3, 5, 6, 7, 8, 9, 10, 13, 15, 16, 18, 21, 22, 24, 36];

    /// <summary>Functions 2/4 (<c>ItemModsSetValueBaseToMax</c>) always apply Max; Min is optional.</summary>
    private static readonly HashSet<int> MaxOnlyFunctions = [2, 4];

    /// <summary><c>ItemModsSetSkillOnAttack</c>: Param = skill, Min = trigger chance (0 → 5%), Max = skill level.</summary>
    private const int EventSkillFunction = 11;

    /// <summary><c>ItemModsSetRandomParam</c>: Min/Max bound the randomly selected parameter (e.g. a skill id).</summary>
    private const int RandomParamFunction = 12;

    /// <summary><c>ItemModsSetSockets</c>: a Min/Max socket range, otherwise Param as the count.</summary>
    private const int SocketsFunction = 14;

    /// <summary><c>ItemModsSetParam</c>: Param when present, otherwise a Min/Max roll, otherwise 0.</summary>
    private const int ParamOrRangeFunction = 17;

    /// <summary><c>ItemModsSetChargedSkill</c>: Param = skill, Min = max charges (0 → 5), Max = skill level.</summary>
    private const int ChargedSkillFunction = 19;

    /// <summary>
    /// Validates <paramref name="slot"/> and records each finding as an error on
    /// <paramref name="result"/> against <paramref name="itemName"/>.
    /// </summary>
    public static void Report<T>(ImportResult<T> result, string itemName, string file, int? parsedRow, SourcePropertySlot slot, GameData data)
    {
        foreach (var message in Validate(file, parsedRow, slot, data))
            result.AddError(Category, itemName, message);
    }

    /// <inheritdoc cref="Report{T}"/>
    public static void Report(PipelineResult pipeline, string itemName, string file, int? parsedRow, SourcePropertySlot slot, GameData data)
    {
        foreach (var message in Validate(file, parsedRow, slot, data))
            pipeline.AddError(Category, itemName, message);
    }

    /// <summary>
    /// Returns one diagnostic per problem in <paramref name="slot"/>. Empty for a valid slot,
    /// an empty or ignored code, a <c>propertygroups.txt</c> reference (its children carry
    /// their own values) and a code without a <c>properties.txt</c> definition.
    /// </summary>
    public static List<string> Validate(string file, int? parsedRow, SourcePropertySlot slot, GameData data)
    {
        if (string.IsNullOrWhiteSpace(slot.Code)) return [];
        if (PropertyMapper.IsIgnored(slot.Code, data.ExportConfig)) return [];
        if (data.PropertyGroups.ContainsKey(slot.Code)) return [];
        if (!data.Properties.TryGetValue(slot.Code, out var definition)) return [];

        List<int> functions = definition.Slots.Count > 0
            ? [.. definition.Slots.Select(static s => s.Function)]
            : definition.Function1 is int function1 && function1 > 0 ? [function1] : [];
        if (functions.Count == 0) return [];
        if (slot.Min is null && slot.Max is null && SetsOnlyFlagStats(definition, functions, data)) return [];

        return [.. FindProblems(functions, slot).Select(problem => Describe(file, parsedRow, slot, problem))];
    }

    /// <summary>
    /// True when every applied <c>properties.txt</c> slot is a value function that sets a 1-bit
    /// ItemStatCost flag (<c>Save Bits</c> 1, e.g. <c>nofreeze</c> → <c>item_cannotbefrozen</c>,
    /// <c>half-freeze</c> → <c>item_halffreezeduration</c>). Such a property is defined by its
    /// presence, so blank Min/Max cells are valid; a partly filled or reversed range is still checked.
    /// </summary>
    private static bool SetsOnlyFlagStats(PropertyEntry definition, List<int> functions, GameData data)
    {
        if (!functions.All(static f => RangeFunctions.Contains(f) || MaxOnlyFunctions.Contains(f))) return false;
        List<string?> stats = definition.Slots.Count > 0 ? [.. definition.Slots.Select(static s => s.Stat)] : [definition.Stat1];
        return stats.All(stat => !string.IsNullOrWhiteSpace(stat)
                                 && data.Stats.TryGetValue(stat, out var entry)
                                 && entry.SaveBits == 1);
    }

    private static IEnumerable<string> FindProblems(List<int> functions, SourcePropertySlot slot)
    {
        if (functions.Contains(EventSkillFunction))
            return CheckSkillEvent(slot);
        if (functions.Contains(ChargedSkillFunction))
            return CheckChargedSkill(slot);

        var first = functions[0];
        return first switch
        {
            RandomParamFunction => CheckRandomParam(slot),
            SocketsFunction => CheckSockets(slot),
            ParamOrRangeFunction => HasParameter(slot) ? [] : CheckRange(slot, first, minRequired: true),
            _ when MaxOnlyFunctions.Contains(first) => CheckRange(slot, first, minRequired: false),
            _ when RangeFunctions.Contains(first) => CheckRange(slot, first, minRequired: true),
            _ => []
        };
    }

    private static IEnumerable<string> CheckRange(SourcePropertySlot slot, int function, bool minRequired)
    {
        var reads = $"properties.txt func {function} reads {slot.MinColumn}/{slot.MaxColumn} as the value range";
        if (slot.Min is null && slot.Max is null)
        {
            var unread = HasParameter(slot) && function != ParamOrRangeFunction
                ? $" ({slot.ParamColumn} is not read as the value)"
                : "";
            yield return $"{slot.MinColumn} and {slot.MaxColumn} are blank, so the property has no value; {reads}{unread}";
            yield break;
        }
        if (slot.Min is null && minRequired)
        {
            yield return $"{slot.MinColumn} is blank while {slot.MaxColumn} is {slot.Max}; {reads}";
            yield break;
        }
        if (slot.Max is null)
        {
            yield return $"{slot.MaxColumn} is blank while {slot.MinColumn} is {slot.Min}; {reads}";
            yield break;
        }
        // Negative ranges are written by magnitude in vanilla data (magicsuffix.txt
        // "of Bashing": dmg-ac -25/-40), so both orders are accepted when neither
        // endpoint is positive.
        if (slot.Min > slot.Max && (slot.Min > 0 || slot.Max > 0))
            yield return $"{slot.MinColumn} {slot.Min} is greater than {slot.MaxColumn} {slot.Max} (reversed range); {reads}";
    }

    private static IEnumerable<string> CheckSkillEvent(SourcePropertySlot slot)
    {
        if (!HasParameter(slot))
            yield return $"{slot.ParamColumn} is blank; properties.txt func 11 reads it as the skill to cast";
        if (slot.Min is null)
            yield return $"{slot.MinColumn} (trigger chance %) is blank; properties.txt func 11 reads it as the chance to cast, "
                         + "and only an explicit 0 selects the engine's 5% default";
        else if (slot.Min < 0)
            yield return $"{slot.MinColumn} (trigger chance %) is negative ({slot.Min}); properties.txt func 11 reads it as the chance to cast";
        if (slot.Max is null)
            yield return $"{slot.MaxColumn} (skill level) is blank; properties.txt func 11 reads it as the skill level, "
                         + "and only an explicit 0 or negative level derives it from the item level";
    }

    private static IEnumerable<string> CheckChargedSkill(SourcePropertySlot slot)
    {
        if (!HasParameter(slot))
            yield return $"{slot.ParamColumn} is blank; properties.txt func 19 reads it as the charged skill";
        if (slot.Min is null)
            yield return $"{slot.MinColumn} (maximum charges) is blank; properties.txt func 19 reads it as the charge count, "
                         + "and only an explicit 0 selects the engine's default of 5 charges";
        if (slot.Max is null)
            yield return $"{slot.MaxColumn} (skill level) is blank; properties.txt func 19 reads it as the skill level";
    }

    private static IEnumerable<string> CheckRandomParam(SourcePropertySlot slot)
    {
        var reads = $"properties.txt func {RandomParamFunction} picks the parameter between {slot.MinColumn} and {slot.MaxColumn}";
        if (slot.Min is null || slot.Max is null)
            yield return $"{(slot.Min is null ? slot.MinColumn : slot.MaxColumn)} is blank; {reads}";
        else if (slot.Min > slot.Max)
            yield return $"{slot.MinColumn} {slot.Min} is greater than {slot.MaxColumn} {slot.Max} (reversed range); {reads}";
    }

    private static IEnumerable<string> CheckSockets(SourcePropertySlot slot)
    {
        var reads = $"properties.txt func {SocketsFunction} rolls sockets between {slot.MinColumn} and {slot.MaxColumn}, "
                    + $"otherwise uses {slot.ParamColumn}";
        if (slot.Min is null && slot.Max is null)
        {
            if (!HasParameter(slot))
                yield return $"{slot.ParamColumn}, {slot.MinColumn} and {slot.MaxColumn} are blank, so no socket count is defined; {reads}";
        }
        else if (slot.Min is null || slot.Max is null)
            yield return $"{(slot.Min is null ? slot.MinColumn : slot.MaxColumn)} is blank while the other endpoint is set; {reads}";
        else if (slot.Min > slot.Max)
            yield return $"{slot.MinColumn} {slot.Min} is greater than {slot.MaxColumn} {slot.Max} (reversed range); {reads}";
    }

    private static bool HasParameter(SourcePropertySlot slot) => !string.IsNullOrWhiteSpace(slot.Parameter);

    private static string Describe(string file, int? parsedRow, SourcePropertySlot slot, string problem) =>
        $"Invalid source property {slot.CodeColumn} '{slot.Code}' in {file}{(parsedRow is { } row ? $" (parsed row {row})" : "")}:"
        + $"\n    Values:  {slot.ParamColumn}={OmittedPropertyLine.Cell(slot.Parameter)}, "
        + $"{slot.MinColumn}={OmittedPropertyLine.Cell(slot.Min)}, {slot.MaxColumn}={OmittedPropertyLine.Cell(slot.Max)}"
        + $"\n    Problem: {problem}."
        + "\n    Action:  the property is exported unchanged; no value was corrected, filled in or dropped.";
}
