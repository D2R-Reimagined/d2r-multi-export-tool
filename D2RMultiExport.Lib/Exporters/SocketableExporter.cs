// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Encodings.Web;
using System.Text.Json;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Exports runes, gems and jewel bases with their finalized host-specific property lines.
/// </summary>
public static class SocketableExporter
{
    /// <summary>Manifest producer name for this independently isolated export step.</summary>
    public const string Producer = "socketables.json";

    /// <summary>Bundle-relative path of the socketable catalog.</summary>
    public const string OutputPath = "keyed/socketables.json";

    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        GameData data,
        bool prettyPrint,
        Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(sources, OutputPath, warn, "misc.txt", "gems.txt", "itemtypes.txt")) return;
        await ExportAsync(outputs, data, prettyPrint, warn);
    }

    public static async Task ExportAsync(
        ExportOutputs outputs,
        GameData data,
        bool prettyPrint,
        Action<string> warn)
    {
        var rows = new List<SocketableRow>();
        foreach (var item in data.MiscItems.Values)
        {
            var classification = FindSocketableType(item, data);
            if (classification is null) continue;
            if (string.IsNullOrWhiteSpace(item.NameStr))
            {
                warn($"misc.txt: socketable code '{item.Code}' has no NameStr and was omitted.");
                continue;
            }

            data.Gems.TryGetValue(item.Code, out var gem);
            rows.Add(new SocketableRow
            {
                Code = item.Code,
                NameKey = data.ResolveBaseItemNameKey(item.Code, item.NameStr),
                Type = classification.Type.Index,
                Kind = classification.Kind,
                ItemLevel = item.Level,
                RequiredLevel = item.LevelReq,
                WeaponLines = MapLines(gem?.WeaponProperties, item.Level, data),
                ArmorLines = MapLines(gem?.HelmProperties, item.Level, data),
                ShieldLines = MapLines(gem?.ShieldProperties, item.Level, data)
            });
        }

        rows.Sort(static (left, right) =>
        {
            var required = left.RequiredLevel.CompareTo(right.RequiredLevel);
            return required != 0 ? required : string.Compare(left.Code, right.Code, StringComparison.Ordinal);
        });

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await outputs.WriteJsonAsync(OutputPath, rows, options);
    }

    private static SocketableType? FindSocketableType(MiscEntry item, GameData data)
    {
        foreach (var code in new[] { item.Type, item.Type2 })
        {
            if (string.IsNullOrWhiteSpace(code) || !data.ItemTypes.TryGetValue(code, out var type)) continue;
            if (TryFindConfiguredKind(type, data, out var kind)) return new SocketableType(type, kind);
        }
        return null;
    }

    private static bool TryFindConfiguredKind(ItemTypeEntry type, GameData data, out string kind)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<ItemTypeEntry>();
        queue.Enqueue(type);
        while (queue.TryDequeue(out var current))
        {
            if (!visited.Add(current.Code)) continue;
            if (data.ExportConfig.SocketableItemKinds.TryGetValue(current.Code, out kind!)
                && !string.IsNullOrWhiteSpace(kind))
            {
                return true;
            }
            foreach (var parentCode in new[] { current.Equiv1, current.Equiv2 })
            {
                if (!string.IsNullOrWhiteSpace(parentCode)
                    && data.ItemTypes.TryGetValue(parentCode, out var parent))
                {
                    queue.Enqueue(parent);
                }
            }
        }
        kind = "";
        return false;
    }

    private static List<KeyedLine> MapLines(
        IReadOnlyList<ItemPropertyValue>? properties,
        int itemLevel,
        GameData data)
    {
        if (properties is not { Count: > 0 }) return [];
        var rows = new List<CubePropertyExport>();
        foreach (var property in properties)
        {
            if (string.IsNullOrEmpty(property.Code)
                || PropertyMapper.IsIgnored(property.Code, data.ExportConfig))
            {
                continue;
            }
            if (PropertyGroupExpander.TryExpand(
                    property.Code,
                    property.Min,
                    property.Max,
                    rows.Count,
                    itemLevel,
                    data,
                    out var groupExpansion))
            {
                rows.AddRange(groupExpansion);
                continue;
            }
            rows.AddRange(PropertyMapper.MapRows(
                property.Code,
                property.Parameter,
                property.Min,
                property.Max,
                data,
                itemLevel,
                rows.Count));
        }
        return [.. PropertyCleanup.CleanupDuplicates(rows, data, itemLevel).SelectMany(static row => row.Lines)];
    }

    private sealed class SocketableRow
    {
        public string Code { get; init; } = "";
        public string NameKey { get; init; } = "";
        public string Type { get; init; } = "";
        public string Kind { get; init; } = "";
        public int ItemLevel { get; init; }
        public int RequiredLevel { get; init; }
        public List<KeyedLine> WeaponLines { get; init; } = [];
        public List<KeyedLine> ArmorLines { get; init; } = [];
        public List<KeyedLine> ShieldLines { get; init; } = [];
    }

    private sealed record SocketableType(ItemTypeEntry Type, string Kind);
}
