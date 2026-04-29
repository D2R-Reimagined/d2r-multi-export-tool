// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Import;

/// <summary>
/// Merges duplicate properties matching the old doc-generator's CleanupDublicates logic.
/// Properties with the same stat key (PropertyCode + Parameter) are summed and re-resolved.
/// </summary>
public static class PropertyCleanup
{
    /// <summary>
    /// Merges duplicate properties in-place, matching the old tool's CleanupDublicates behavior.
    /// Groups properties by (PropertyCode, Suffix), sums their Min/Max, and re-resolves the display string.
    /// Also transforms "+X to Minimum Weapon Damage" ? "Adds X Weapon Damage".
    /// </summary>
    /// <summary>
    /// Split elemental damage codes (min/max/len) that ItemStatCost typically renders
    /// as bare numbers (descfunc 0). They must survive UniqueImporter's bare-int filter
    /// so MergeElementalTriplets can aggregate them into dmg-{fire,cold,ltng,pois} entries.
    /// Backed by <see cref="Config.ExportConfig.SplitElementalCodesSet"/>
    /// (<c>export-config.json → splitElementalCodes</c>).
    /// </summary>
    public static bool IsSplitElementalCode(string? code, Config.ExportConfig config) =>
        !string.IsNullOrEmpty(code) && config.SplitElementalCodesSet.Contains(code);

    /// <summary>
    /// Merges duplicate properties in-place and returns the cleaned list.
    /// </summary>
    /// <param name="properties">Properties to merge / sort.</param>
    /// <param name="data">Shared game data — used to re-resolve display strings on merge.</param>
    /// <param name="itemLevel">Item level for property re-resolution.</param>
    /// <param name="suffixBucketOrder">
    /// Optional comparator over the trimmed <c>Suffix</c> token (e.g. <c>"(Weapon)"</c>,
    /// <c>"(Armor)"</c>, <c>"(Shield)"</c>). When supplied, suffixed-property buckets are
    /// emitted in the order it dictates; otherwise insertion order is preserved. Used by
    /// runewords to enforce the legacy doc-generator ordering (Weapon → Armor → Shield → other).
    /// </param>
    public static List<CubePropertyExport> CleanupDuplicates(
        List<CubePropertyExport> properties,
        GameData data,
        int itemLevel,
        IComparer<string>? suffixBucketOrder = null)
    {
        if (properties.Count == 0) return properties;

        // Merge elemental triplets (cold-min+cold-max+cold-len ? "Adds X-Y Weapon Cold Damage", etc.)
        MergeElementalTriplets(properties, data);

        // Merge dmg-min + dmg-max pairs into a single row: the 2-arg case uses the
        // game-native strModMinDamageRange template ("Adds %d-%d Damage"); the 4-arg
        // level-scaled case uses the synthetic strDamageMergedRange ("Adds %d-%d to %d-%d Damage")
        // since the game has no equivalent shape. Restores the legacy doc-generator's
        // "Adds X-Y Damage" / "Adds X-Y to A-B Damage" output.
        MergeMinMaxDamage(properties);

        // Separate base props (no suffix) from suffixed props
        var baseProps = properties.Where(p => string.IsNullOrEmpty(p.Suffix)).ToList();
        var suffixedProps = properties.Where(p => !string.IsNullOrEmpty(p.Suffix)).ToList();

        // Merge base props with duplicate (PropertyCode + Parameter) - e.g. two dmg-min entries
        // but NOT two different "skill" entries with different skill parameters
        // and NOT "rip" (random skill) entries which represent different class skill pools.
        // The blocklist is config-driven (export-config.json -> propertyMergeBlocklist).
        var neverMerge = data.ExportConfig.PropertyMergeBlocklistSet;
        var duplicateGroups = baseProps
            .Where(p => !neverMerge.Contains(p.PropertyCode ?? ""))
            .GroupBy(p => (p.PropertyCode ?? "") + "|" + (p.Parameter ?? ""))
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in duplicateGroups)
        {
            var items = group.ToList();
            var merged = MergePropertyGroup(items, data, itemLevel);
            var groupSet = new HashSet<CubePropertyExport>(items);
            baseProps.RemoveAll(p => groupSet.Contains(p));
            baseProps.Add(merged);
        }

        // Merge suffixed props by suffix category and normalized property string
        var result = new List<CubePropertyExport>();
        result.AddRange(baseProps.OrderByDescending(p => p.Priority).ThenBy(p => p.Index));

        var suffixGroups = suffixedProps.GroupBy(p => p.Suffix.Trim()).ToList();
        if (suffixBucketOrder != null)
        {
            suffixGroups = suffixGroups.OrderBy(g => g.Key, suffixBucketOrder).ToList();
        }
        foreach (var suffixGroup in suffixGroups)
        {
            var merged = MergeDuplicatesWithinSuffixBucket(suffixGroup.ToList(), data, itemLevel);
            result.AddRange(merged);
        }

        return result;
    }

    /// <summary>
    /// Suffix-bucket comparator used by <see cref="RunewordImporter"/>: emits
    /// <c>(Weapon)</c> → <c>(Armor)</c> → <c>(Shield)</c> → any other token, matching
    /// the legacy doc-generator's runeword output ordering.
    /// </summary>
    public static readonly IComparer<string> RunewordSuffixOrder =
        Comparer<string>.Create((a, b) => RunewordSuffixRank(a).CompareTo(RunewordSuffixRank(b)));

    private static int RunewordSuffixRank(string suffix) => suffix switch
    {
        "(Weapon)" => 0,
        "(Armor)"  => 1,
        "(Shield)" => 2,
        _          => 3,
    };

    private static CubePropertyExport MergePropertyGroup(List<CubePropertyExport> group, GameData data, int itemLevel)
    {
        var first = group[0];
        int? sumMin = group.Sum(p => p.Min ?? 0);
        int? sumMax = group.Sum(p => p.Max ?? 0);

        // Re-resolve with summed values
        var resolved = PropertyMapper.Map(first.PropertyCode!, first.Parameter, sumMin, sumMax, data, itemLevel);

        var merged = new CubePropertyExport
        {
            Index = first.Index,
            PropertyCode = first.PropertyCode,
            Priority = resolved.Priority,
            Min = sumMin,
            Max = sumMax,
            Parameter = first.Parameter,
            IsEase = first.IsEase,
            Suffix = first.Suffix,
            Lines = resolved.Lines
        };

        return merged;
    }

    /// <summary>
    /// Merges elemental min+max+len triplets into single "Adds X-Y Weapon [Element] Damage" entries,
    /// matching the old doc-generator's GetProperties aggregation logic.
    /// </summary>
    private static void MergeElementalTriplets(List<CubePropertyExport> properties, GameData data)
    {
        // Cold: merge min+max (with or without len). Poison: require len.
        // Fire/Lightning stay as separate min/max entries (matching old code).
        // Fire/Lightning have no duration component, so lenCode is null.
        // Cold has a duration in source data but the rendered tooltip drops it.
        // Poison merges min+max+len into total damage over N seconds.
        var elemDefs = new (string minCode, string maxCode, string? lenCode, string element, int priority, bool requireLen)[]
        {
            ("fire-min", "fire-max", null,       "Fire",      100, false),
            ("ltng-min", "ltng-max", null,       "Lightning",  98, false),
            ("cold-min", "cold-max", "cold-len", "Cold",       96, false),
            ("pois-min", "pois-max", "pois-len", "Poison",     92, false),
        };

        foreach (var (minCode, maxCode, lenCode, element, priority, requireLen) in elemDefs)
        {
            var minProp = properties.FirstOrDefault(p => string.Equals(p.PropertyCode, minCode, StringComparison.OrdinalIgnoreCase));
            var maxProp = properties.FirstOrDefault(p => string.Equals(p.PropertyCode, maxCode, StringComparison.OrdinalIgnoreCase));
            var lenProp = lenCode != null ? properties.FirstOrDefault(p => string.Equals(p.PropertyCode, lenCode, StringComparison.OrdinalIgnoreCase)) : null;

            // Drop orphaned length properties
            if (lenProp != null && minProp == null && maxProp == null)
            {
                properties.Remove(lenProp);
                continue;
            }

            // Merge when min+max present (and len if required)
            bool canMerge = minProp != null && maxProp != null;
            if (canMerge && requireLen && lenProp == null)
                canMerge = false;

            if (canMerge)
            {
                var minVal = minProp!.Min ?? 0;
                var maxVal = maxProp!.Max ?? maxProp.Min ?? 0;
                int? secondsArg = null;

                if (element == "Poison" && lenProp != null)
                {
                    var frames = lenProp.Min ?? lenProp.Max ?? 0;
                    var poisMin = (int)Math.Ceiling(minVal * frames / 256.0);
                    var poisMax = (int)Math.Ceiling(maxVal * frames / 256.0);
                    var seconds = frames / 25.0;
                    minProp.Parameter = frames.ToString();
                    minVal = poisMin;
                    maxVal = poisMax;
                    secondsArg = (int)Math.Round(seconds);
                }

                var mergedCode = element switch
                {
                    "Fire"      => "dmg-fire",
                    "Cold"      => "dmg-cold",
                    "Lightning" => "dmg-ltng",
                    "Poison"    => "dmg-pois",
                    _           => $"dmg-{element.ToLowerInvariant()}"
                };
                minProp.PropertyCode = mergedCode;
                minProp.Priority = priority;
                minProp.Min = minVal;
                minProp.Max = maxVal;

                var earliestIndex = minProp.Index;
                if (maxProp.Index < earliestIndex) earliestIndex = maxProp.Index;
                if (lenProp != null && lenProp.Index < earliestIndex) earliestIndex = lenProp.Index;
                minProp.Index = earliestIndex;

                // Regenerate the keyed-line for the merged row. Without this the
                // wire-side Lines list keeps the original "cold-min"/"pois-min"
                // resolution (e.g. ModStr1t "+5 to Minimum Cold Damage") which
                // both selects the wrong template and silently drops the max
                // half of the range.
                minProp.Lines = element switch
                {
                    "Fire"      => [new KeyedLine { Key = "strModFireDamageRange",      Args = [minVal, maxVal] }],
                    "Lightning" => [new KeyedLine { Key = "strModLightningDamageRange", Args = [minVal, maxVal] }],
                    "Cold"      => [new KeyedLine { Key = "strModColdDamageRange",      Args = [minVal, maxVal] }],
                    "Poison"    => [new KeyedLine
                    {
                        Key = "strModPoisonDamageRange",
                        Args = [minVal, maxVal, secondsArg ?? 0]
                    }],
                    _ => minProp.Lines
                };

                properties.Remove(maxProp);
                if (lenProp != null) properties.Remove(lenProp);
            }
            else
            {
                if (lenProp != null) properties.Remove(lenProp);
            }
        }
    }

    /// <summary>
    /// Merges paired dmg-min and dmg-max base properties into a single row. The
    /// fixed 2-arg case uses the game-native strModMinDamageRange template; the
    /// level-scaled 4-arg case uses the synthetic strDamageMergedRange template
    /// (the game has no equivalent shape). This restores the legacy
    /// "Adds X-Y Damage" / "Adds X-Y to A-B Damage" output for items like
    /// runewords (Steel) and set items (Isenhart's Lightbrand) where the underlying
    /// data carries dmg-min and dmg-max as separate stat entries.
    /// </summary>
    public static void MergeMinMaxDamage(List<CubePropertyExport> properties)
    {
        if (properties.Count == 0) return;

        // Pair within the same Suffix scope so " (Weapon)" / " (Shield)" / " (Armor)"
        // gem-property contributions merge independently.
        var suffixGroups = properties
            .GroupBy(p => p.Suffix ?? "")
            .Select(g => g.ToList())
            .ToList();

        foreach (var group in suffixGroups)
        {
            var minProp = group.FirstOrDefault(p => string.Equals(p.PropertyCode, "dmg-min", StringComparison.OrdinalIgnoreCase));
            var maxProp = group.FirstOrDefault(p => string.Equals(p.PropertyCode, "dmg-max", StringComparison.OrdinalIgnoreCase));
            if (minProp == null || maxProp == null) continue;

            int minLow = minProp.Min ?? 0;
            int minHigh = minProp.Max ?? minLow;
            int maxLow = maxProp.Min ?? 0;
            int maxHigh = maxProp.Max ?? maxLow;

            // Preserve any qualifier ("(Weapon)" etc.) carried on the existing line.
            string? qualifier = minProp.Lines?.FirstOrDefault()?.Qualifier
                                ?? maxProp.Lines?.FirstOrDefault()?.Qualifier;

            KeyedLine line;
            if (minLow == minHigh && maxLow == maxHigh)
            {
                line = new KeyedLine
                {
                    Key = "strModMinDamageRange",
                    Args = [minLow, maxLow],
                    Qualifier = qualifier
                };
            }
            else
            {
                line = new KeyedLine
                {
                    Key = SyntheticStringRegistry.Keys.DamageMergedRange,
                    Args = [minLow, minHigh, maxLow, maxHigh],
                    Qualifier = qualifier
                };
            }

            minProp.Lines = new List<KeyedLine> { line };
            minProp.Min = minLow;
            minProp.Max = maxHigh;
            minProp.Priority = Math.Max(minProp.Priority, maxProp.Priority);
            if (maxProp.Index < minProp.Index) minProp.Index = maxProp.Index;

            properties.Remove(maxProp);
        }
    }

    /// <summary>
    /// Merges duplicate properties within an already-suffix-scoped bucket and
    /// returns the bucket sorted by descending priority then ascending index —
    /// matching the legacy behaviour without depending on the long-removed
    /// English `PropertyString`. Two properties are considered duplicates when
    /// they share the same <see cref="CubePropertyExport.PropertyCode"/> and
    /// <see cref="CubePropertyExport.Parameter"/>.
    /// </summary>
    private static List<CubePropertyExport> MergeDuplicatesWithinSuffixBucket(List<CubePropertyExport> props, GameData data, int itemLevel)
    {
        return props
            .GroupBy(p => (p.PropertyCode ?? "") + "|" + (p.Parameter ?? ""), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? MergePropertyGroup(g.ToList(), data, itemLevel) : g.First())
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Index)
            .ToList();
    }
}

