// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Shared expansion of <c>propertygroups.txt</c> entries referenced from any
/// importer's property list (uniques, sets, runewords, cube recipes, magic
/// affixes, automagic). Produces a single parent <see cref="CubePropertyExport"/>
/// whose <see cref="CubePropertyExport.Lines"/> contains one parent
/// <see cref="KeyedLine"/>:
/// <list type="bullet">
///   <item><see cref="KeyedLine.Code"/> — raw English group code (structural
///   identifier, ignored by the missing-translations audit).</item>
///   <item><see cref="KeyedLine.NameKey"/> — synthetic
///   <c>strPropertyGroupsProperty</c> ("Random Grouped Affix") that the
///   website resolves for the bucket label.</item>
///   <item><see cref="KeyedLine.PickMode"/> — the group's <c>PickMode</c>
///   column.</item>
///   <item><see cref="KeyedLine.RollCount"/> — the referencing source slot's
///   outer <c>min</c>/<c>max</c> (how many children the group rolls).</item>
///   <item><see cref="KeyedLine.Children"/> — the resolved sub-property
///   <see cref="KeyedLine"/>s, each carrying its own <see cref="KeyedLine.Chance"/>.</item>
/// </list>
/// The parent/child hierarchy is part of the website contract — do not
/// flatten children into siblings or stamp <c>pickMode</c> on every child.
/// </summary>
public static class PropertyGroupExpander
{
    /// <summary>
    /// If <paramref name="propertyCode"/> resolves to a known
    /// <c>propertygroups.txt</c> entry, expands it into a one-element
    /// <see cref="CubePropertyExport"/> list and returns <c>true</c>.
    /// Otherwise returns <c>false</c> with an empty <paramref name="results"/>.
    /// <paramref name="rollMin"/>/<paramref name="rollMax"/> are the outer
    /// <c>min</c>/<c>max</c> cells of the referencing source slot; they are
    /// exported as the parent's <see cref="KeyedLine.RollCount"/>.
    /// </summary>
    public static bool TryExpand(string? propertyCode, int? rollMin, int? rollMax, int index, int itemLevel, GameData data,
        out List<CubePropertyExport> results)
    {
        results = [];
        if (string.IsNullOrEmpty(propertyCode)) return false;
        if (!data.PropertyGroups.TryGetValue(propertyCode, out var group)) return false;

        results = Expand(group, index, itemLevel, data, rollMin, rollMax);
        return true;
    }

    /// <summary>
    /// Expand a resolved <see cref="PropertyGroupEntry"/> into a parent
    /// <see cref="CubePropertyExport"/>. Returns an empty list when the group
    /// has no resolvable sub-properties (matches doc-generator behaviour: a
    /// property group with no children produces no on-wire entry).
    /// </summary>
    public static List<CubePropertyExport> Expand(PropertyGroupEntry group, int index, int itemLevel, GameData data,
        int? rollMin = null, int? rollMax = null)
    {
        // Stringify the group's PickMode once so it can live on the parent line.
        // Null when the group has no PickMode column populated (rare — treated
        // as "always-on", no pickMode/chance written).
        var pickModeStr = group.PickMode?.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var children = new List<KeyedLine>();

        foreach (var sub in group.SubProperties)
        {
            if (string.IsNullOrEmpty(sub.Property)) continue;
            if (PropertyMapper.IsIgnored(sub.Property, data.ExportConfig)) continue;

            try
            {
                foreach (var resolved in PropertyMapper.MapExpanded(sub.Property, sub.Parameter, sub.Min, sub.Max, data,
                             itemLevel))
                {
                    var component = resolved.PropertyCode;
                    // Skip rows that produced no keyed line and aren't recognised in
                    // Properties.txt — same gate as the main MapProperties loop, so
                    // unknown sub-property codes don't leak through silently.
                    if (resolved.Lines.Count == 0
                        && !data.Properties.ContainsKey(component)
                        && !PropertyCleanup.IsSplitElementalCode(component, data.ExportConfig))
                        continue;

                    // Stamp the per-row ChanceN value on every line resolved for
                    // this sub-property. The parent's PickMode tells the consumer
                    // how to interpret the value; we don't repeat it on each child.
                    // chance==0 / null means "always-on" — leave the field unset.
                    foreach (var line in resolved.Lines)
                    {
                        if (sub.Chance is > 0)
                            line.Chance = sub.Chance;

                        // Explicit aliases preserve the existing behavior for
                        // class-restricted groups whose identifiers do not encode
                        // a class range directly.
                        if (data.ExportConfig.CustomSkillTabAliases.TryGetValue(group.Code, out var aliasClassCode)
                            && data.CharStatsByCode.ContainsKey(aliasClassCode)
                            && (line.Key == Translation.SyntheticStringRegistry.Keys.SkillTabBonusClassOnly
                                || line.Key == Translation.SyntheticStringRegistry.Keys.SkillTabRandomClassOnly))
                        {
                            line.Key = Translation.SyntheticStringRegistry.Keys.SkillTabRandomClassOnly;
                            line.Args = line.Args.TakeWhile(static arg => arg is int or double).ToArray();
                            line.ClassOnly = Translation.PropertyKeyResolver.TryGetClassOnlyKey(aliasClassCode);
                        }
                        else if (GroupedSkillTabClassOnlyKey(line, data) is { } classOnlyKey)
                        {
                            line.Key = Translation.SyntheticStringRegistry.Keys.SkillTabRandomClassOnly;
                            line.Args = line.Args.TakeWhile(static arg => arg is int or double).ToArray();
                            line.ClassOnly = classOnlyKey;
                        }

                        children.Add(line);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"propertygroups.txt '{group.Code}', property '{sub.Property}': {ex.Message}", ex);
            }
        }

        // Empty group → emit nothing (matches doc-generator: a property group
        // with no resolvable sub-properties produces no on-wire entry).
        if (children.Count == 0) return [];

        var parent = new KeyedLine
        {
            // Parent has no translation key/args of its own; the consumer
            // detects parent-ness by the presence of `code` + `children`.
            // `NameKey` resolves to the synthetic "Random Grouped Affix"
            // label so the website renders a localized header for the
            // bucket while still branching on the raw `Code` identifier.
            Code = group.Code,
            NameKey = Translation.SyntheticStringRegistry.Keys.PropertyGroupsProperty,
            PickMode = pickModeStr,
            // Outer source-slot min/max: how many children this group rolls.
            RollCount = PropertyGroupRollCount.From(rollMin, rollMax),
            Children = children
        };

        return
        [
            new CubePropertyExport
            {
                Index = index,
                PropertyCode = group.Code,
                // Force propertygroup affixes to sort AFTER all standard
                // properties (which use real ItemStatCost descPriority values,
                // always >= 0). FlattenPropertiesByPriority sorts by Priority
                // descending with source-index as a stable tiebreaker, so
                // int.MinValue here pushes every group parent to the bottom
                // while preserving the on-item order between groups.
                Priority = int.MinValue,
                PickMode = pickModeStr,
                Lines = [parent]
            }
        ];
    }

    /// <summary>
    /// Resolves the class suffix for a grouped random skill-tab child only when
    /// its identifier bounds form a valid range within one configured class.
    /// </summary>
    private static string? GroupedSkillTabClassOnlyKey(KeyedLine line, GameData data)
    {
        var selection = line.Selection;
        if (line.Key != Translation.SyntheticStringRegistry.Keys.SkillTabBonusClassOnly
            || selection is null
            || !string.Equals(selection.Stat, "item_addskill_tab", StringComparison.OrdinalIgnoreCase)
            || selection.Min is not int tabMin
            || selection.Max is not int tabMax
            || tabMin < 0
            || tabMax < tabMin
            || tabMin / 3 != tabMax / 3)
            return null;

        var classCode = ClassRangeConfig.CodeForClassIndex(tabMin / 3);
        if (classCode is null || !data.CharStatsByCode.ContainsKey(classCode))
            return null;

        return Translation.PropertyKeyResolver.TryGetClassOnlyKey(classCode);
    }
}
