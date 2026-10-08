// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Lib.Import;

/// <summary>Merges additive rolls before pairing damage components, retaining activation scopes.</summary>
public static class PropertyCleanup
{
    /// <summary>Split damage helpers must survive until their components can be paired.</summary>
    public static bool IsSplitElementalCode(string? code, Config.ExportConfig config) =>
        !string.IsNullOrEmpty(code) && config.SplitElementalCodesSet.Contains(code);

    /// <summary>
    /// Sums additive duplicate rolls within the same activation scope, then combines
    /// damage components. Independent chance/level and charge/level fields are never summed.
    /// </summary>
    public static List<CubePropertyExport> CleanupDuplicates(
        List<CubePropertyExport> properties,
        GameData data,
        int itemLevel,
        IComparer<string>? suffixBucketOrder = null)
    {
        if (properties.Count == 0) return properties;
        var merged = properties
            .GroupBy(p => CanSum(p, data) ? MergeIdentity(p) : $"unmerged:{properties.IndexOf(p)}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? MergePropertyGroup(g.ToList(), data, itemLevel) : g.First())
            .ToList();
        // Pairing first and subsequently re-resolving a duplicate as dmg-min erased
        // the maximum damage component. Elemental pairing had the same ordering issue.
        MergeElementalTriplets(merged);
        MergeMinMaxDamage(merged);
        var result = merged.Where(p => string.IsNullOrEmpty(p.Suffix))
            .OrderByDescending(p => p.Priority).ThenBy(p => p.Index).ToList();
        var scopes = merged.Where(p => !string.IsNullOrEmpty(p.Suffix)).GroupBy(p => p.Suffix.Trim());
        if (suffixBucketOrder != null)
        {
            scopes = scopes.OrderBy(g => g.Key, suffixBucketOrder);
        }
        foreach (var scope in scopes)
        {
            result.AddRange(scope.OrderByDescending(p => p.Priority).ThenBy(p => p.Index));
        }
        return result;
    }

    /// <summary>Engine runeword equipment scopes in their established display order.</summary>
    public static readonly IComparer<string> RunewordSuffixOrder =
        Comparer<string>.Create((a, b) => RunewordSuffixRank(a).CompareTo(RunewordSuffixRank(b)));

    private static int RunewordSuffixRank(string suffix) => suffix switch
    {
        "(Weapon)" => 0,
        "(Armor)" => 1,
        "(Shield)" => 2,
        _ => 3
    };

    private static bool CanSum(CubePropertyExport property, GameData data) =>
        !data.ExportConfig.PropertyMergeBlocklistSet.Contains(property.PropertyCode ?? "")
        && !data.PropertyGroups.ContainsKey(property.PropertyCode ?? "")
        // Fixed engine encodings: event chance/level, random selection and charged
        // skill count/level are independent fields, not additive roll endpoints.
        && (!data.Properties.TryGetValue(property.PropertyCode ?? "", out var definition)
            || definition.Function1 is not (11 or 12 or 19));

    private static string ActivationIdentity(CubePropertyExport p) =>
        $"{p.Suffix}|{p.NumberOfItemsRequired}|{p.ModChance}|{p.PickMode}|{p.Chance}|{p.Lines.FirstOrDefault()?.Qualifier}|{p.Lines.FirstOrDefault()?.FullSet}";

    private static string MergeIdentity(CubePropertyExport p) =>
        $"{p.PropertyCode}|{p.Parameter}|{ActivationIdentity(p)}";

    private static CubePropertyExport MergePropertyGroup(List<CubePropertyExport> group, GameData data, int itemLevel)
    {
        var first = group[0];
        int? sumMin = group.All(p => !p.Min.HasValue && !p.Max.HasValue) ? null : group.Sum(p => p.Min ?? p.Max ?? 0);
        int? sumMax = group.All(p => !p.Max.HasValue) ? null : group.Sum(p => p.Max ?? p.Min ?? 0);
        var parameter = first.Parameter;
        if (data.Properties.TryGetValue(first.PropertyCode ?? "", out var definition)
            && definition.Function1 == 17 && int.TryParse(parameter, out var value))
        {
            parameter = checked(value * group.Count).ToString(CultureInfo.InvariantCulture);
        }
        var resolved = PropertyMapper.Map(first.PropertyCode!, parameter, sumMin, sumMax, data, itemLevel);
        var merged = new CubePropertyExport
        {
            Index = group.Min(p => p.Index),
            PropertyCode = first.PropertyCode,
            Priority = resolved.Priority,
            Min = sumMin,
            Max = sumMax,
            Parameter = parameter,
            IsEase = first.IsEase,
            ModChance = first.ModChance,
            NumberOfItemsRequired = first.NumberOfItemsRequired,
            PickMode = first.PickMode,
            Chance = first.Chance,
            Suffix = first.Suffix,
            Lines = resolved.Lines
        };
        Stamp(merged, first.Lines.FirstOrDefault());
        return merged;
    }

    private static void Stamp(CubePropertyExport row, KeyedLine? original)
    {
        foreach (var line in row.Lines)
        {
            line.Qualifier = original?.Qualifier;
            line.ItemsRequired = original?.ItemsRequired ?? row.NumberOfItemsRequired;
            line.FullSet = original?.FullSet ?? false;
            line.Chance = original?.Chance ?? row.Chance;
        }
    }

    /// <summary>Established elemental pairing order and priorities; each component can itself roll.</summary>
    private static readonly (string Min, string Max, string? Length, string Code, string Key, string RollsKey, int Priority)[] ElementalPairs =
    [
        ("fire-min", "fire-max", null, "dmg-fire", "strModFireDamageRange", SyntheticStringRegistry.Keys.FireDamageRolls, 100),
        ("ltng-min", "ltng-max", null, "dmg-ltng", "strModLightningDamageRange", SyntheticStringRegistry.Keys.LightningDamageRolls, 98),
        ("cold-min", "cold-max", "cold-len", "dmg-cold", "strModColdDamageRange", SyntheticStringRegistry.Keys.ColdDamageRolls, 96),
        ("pois-min", "pois-max", "pois-len", "dmg-pois", "strModPoisonDamageRange", SyntheticStringRegistry.Keys.PoisonDamageRolls, 92)
    ];

    private static void MergeElementalTriplets(List<CubePropertyExport> properties)
    {
        foreach (var pair in ElementalPairs)
        {
            foreach (var scope in properties.GroupBy(ActivationIdentity).Select(g => g.ToList()).ToList())
            {
                var min = scope.FirstOrDefault(p => string.Equals(p.PropertyCode, pair.Min, StringComparison.OrdinalIgnoreCase));
                var max = scope.FirstOrDefault(p => string.Equals(p.PropertyCode, pair.Max, StringComparison.OrdinalIgnoreCase));
                var length = scope.FirstOrDefault(p => pair.Length != null && string.Equals(p.PropertyCode, pair.Length, StringComparison.OrdinalIgnoreCase));
                if (min == null || max == null) continue;
                if ((!min.Min.HasValue && !min.Max.HasValue) || (!max.Min.HasValue && !max.Max.HasValue)) continue;
                // Keep a duration roll separate instead of guessing a single duration.
                if (length?.Min.HasValue == true && length.Max.HasValue && length.Min != length.Max) continue;
                var minLow = min.Min ?? min.Max!.Value;
                var minHigh = min.Max ?? minLow;
                var maxLow = max.Min ?? max.Max!.Value;
                var maxHigh = max.Max ?? maxLow;
                var ranged = minLow != minHigh || maxLow != maxHigh;
                object[] args = ranged ? [minLow, minHigh, maxLow, maxHigh] : [minLow, maxLow];
                if (pair.Code == "dmg-pois")
                {
                    if (length == null) continue;
                    var frames = length.Min ?? length.Max;
                    if (!frames.HasValue) continue;
                    min.Parameter = frames.Value.ToString(CultureInfo.InvariantCulture);
                    args = [.. args.Select(v => (object)(int)Math.Ceiling((int)v * (double)frames.Value / 256)), frames.Value / 25d];
                }
                var original = min.Lines.FirstOrDefault() ?? max.Lines.FirstOrDefault();
                min.PropertyCode = pair.Code;
                min.Priority = pair.Priority;
                // Raw rates stay internal; only keyed args are converted to total
                // poison damage. Converting both caused a second conversion later.
                min.Min = minLow;
                min.Max = maxHigh;
                min.DamageRolls = (minLow, minHigh, maxLow, maxHigh);
                min.Index = Math.Min(min.Index, max.Index);
                min.Lines = [new KeyedLine { Key = ranged ? pair.RollsKey : pair.Key, Args = args }];
                Stamp(min, original);
                properties.Remove(max);
                if (length != null && pair.Code == "dmg-pois")
                {
                    properties.Remove(length);
                }
            }
        }
    }

    /// <summary>Pairs physical damage components without changing the raw rows used by equipment math.</summary>
    public static void MergeMinMaxDamage(List<CubePropertyExport> properties)
    {
        foreach (var scope in properties.GroupBy(ActivationIdentity).Select(g => g.ToList()).ToList())
        {
            var min = scope.FirstOrDefault(p => string.Equals(p.PropertyCode, "dmg-min", StringComparison.OrdinalIgnoreCase));
            var max = scope.FirstOrDefault(p => string.Equals(p.PropertyCode, "dmg-max", StringComparison.OrdinalIgnoreCase));
            if (min == null || max == null) continue;
            if ((!min.Min.HasValue && !min.Max.HasValue) || (!max.Min.HasValue && !max.Max.HasValue)) continue;
            var minLow = min.Min ?? min.Max!.Value;
            var minHigh = min.Max ?? minLow;
            var maxLow = max.Min ?? max.Max!.Value;
            var maxHigh = max.Max ?? maxLow;
            var ranged = minLow != minHigh || maxLow != maxHigh;
            var original = min.Lines.FirstOrDefault() ?? max.Lines.FirstOrDefault();
            min.Lines = [new KeyedLine
            {
                Key = ranged ? SyntheticStringRegistry.Keys.DamageMergedRange : "strModMinDamageRange",
                Args = ranged ? [minLow, minHigh, maxLow, maxHigh] : [minLow, maxLow]
            }];
            Stamp(min, original);
            max.Lines = [];
            min.Priority = Math.Max(min.Priority, max.Priority);
            min.Index = Math.Min(min.Index, max.Index);
        }
    }
}
