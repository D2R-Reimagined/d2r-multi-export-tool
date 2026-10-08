// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Per-run record of every sprite/icon the asset exporters could not resolve, every
/// supported fallback they used instead, and every output-path collision. Individual
/// entries are kept for <c>extras/asset-report.txt</c>; the run log and
/// <c>import-report.txt</c> get one grouped line per (exporter, reason) so a single shared
/// root cause does not produce hundreds of warnings.
/// </summary>
public sealed class AssetDiagnostics
{
    /// <summary>One unresolved asset, fallback or collision.</summary>
    /// <param name="Exporter">The producing step (<c>item-presentation.json</c>, <c>skill-icons</c>).</param>
    /// <param name="Identifier">Item code / unique / set / skill the asset belongs to.</param>
    /// <param name="Mapping">The asset mapping that was looked up (e.g. <c>armor/helm/cap</c>), if any.</param>
    /// <param name="Source">The resolved source path, or the expected one when the lookup failed.</param>
    /// <param name="Searched">Every location that was checked.</param>
    /// <param name="Reason">Why the lookup failed (the grouping key).</param>
    /// <param name="Fallback">What was used instead, or <c>none</c>.</param>
    /// <param name="Kind">Missing, Fallback or Collision.</param>
    public sealed record Entry(
        string Exporter,
        string Identifier,
        string? Mapping,
        string? Source,
        string Searched,
        string Reason,
        string Fallback,
        AssetIssueKind Kind);

    private readonly List<Entry> _entries = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_lock) return [.. _entries];
        }
    }

    public void Add(Entry entry)
    {
        lock (_lock) _entries.Add(entry);
    }

    public int Count(string exporter, AssetIssueKind kind)
    {
        lock (_lock) return _entries.Count(e => e.Exporter == exporter && e.Kind == kind);
    }

    /// <summary>Reports one grouped warning per (reason) for <paramref name="exporter"/>.</summary>
    public void Summarize(string exporter, Action<string> warn)
    {
        foreach (var group in Entries.Where(e => e.Exporter == exporter && e.Kind != AssetIssueKind.Fallback)
                     .GroupBy(static e => (e.Kind, e.Reason))
                     .OrderBy(static g => g.Key.Kind).ThenByDescending(static g => g.Count()))
        {
            var sample = string.Join(", ", group.Select(static e => e.Identifier).Distinct().Take(5));
            var noun = group.Key.Kind == AssetIssueKind.Collision ? "output collision(s)" : "asset(s) unresolved";
            warn($"{group.Count()} {noun}: {group.Key.Reason}. First: {sample}. Details: extras/asset-report.txt.");
        }
    }

    /// <summary>Renders the full per-entry report, grouped by exporter, kind and reason.</summary>
    public string Render()
    {
        var entries = Entries;
        var builder = new StringBuilder();
        var missing = entries.Count(static e => e.Kind == AssetIssueKind.Missing);
        var fallbacks = entries.Count(static e => e.Kind == AssetIssueKind.Fallback);
        var collisions = entries.Count(static e => e.Kind == AssetIssueKind.Collision);
        builder.Append("=== Asset Report: ").Append(missing).Append(" unresolved, ")
            .Append(fallbacks).Append(" fallback(s), ").Append(collisions).Append(" collision(s) ===\n");
        builder.Append("Unresolved = no sprite was exported for the identifier (its Sprite/Icon is absent).\n");
        builder.Append("Fallback   = a supported substitute was exported (e.g. the base item's art for a unique without its own).\n");
        builder.Append("Collision  = two different sources resolved to the same output file; the first was kept.\n\n");

        foreach (var group in entries.GroupBy(static e => (e.Exporter, e.Kind, e.Reason))
                     .OrderBy(static g => g.Key.Exporter, StringComparer.Ordinal)
                     .ThenBy(static g => g.Key.Kind)
                     .ThenByDescending(static g => g.Count()))
        {
            builder.Append("--- ").Append(group.Key.Exporter).Append(" / ").Append(group.Key.Kind)
                .Append(" (").Append(group.Count()).Append("): ").Append(group.Key.Reason).Append(" ---\n");
            foreach (var entry in group.OrderBy(static e => e.Identifier, StringComparer.Ordinal))
            {
                builder.Append("  ").Append(entry.Identifier);
                if (entry.Mapping is not null) builder.Append("  mapping=").Append(entry.Mapping);
                if (entry.Source is not null) builder.Append("  source=").Append(entry.Source);
                if (entry.Searched.Length > 0) builder.Append("  searched=").Append(entry.Searched);
                builder.Append("  fallback=").Append(entry.Fallback).Append('\n');
            }
            builder.Append('\n');
        }
        return builder.ToString();
    }
}

public enum AssetIssueKind
{
    Missing,
    Collision,
    Fallback
}
