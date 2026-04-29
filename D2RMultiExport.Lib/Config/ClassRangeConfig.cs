// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Config;

/// <summary>
/// Configuration-driven class identity tables loaded from
/// <c>Config/class-ranges.json</c>. Replaces the previously hardcoded class
/// index array and skill-id range switch in <c>PropertyKeyResolver</c>, so the
/// mod can ship new classes (or shift the Warlock id band) without a recompile.
/// </summary>
public static class ClassRangeConfig
{
    /// <summary>Default fallback values used when no JSON file is present (mirrors the original hardcoded constants).</summary>
    private static readonly string[] DefaultClassOrder =
        ["ama", "sor", "nec", "pal", "bar", "dru", "ass", "war"];

    private static readonly List<ClassIdRange> DefaultIdRanges =
    [
        new() { Code = "ama", Min = 6,   Max = 35  },
        new() { Code = "sor", Min = 36,  Max = 65  },
        new() { Code = "nec", Min = 66,  Max = 95  },
        new() { Code = "pal", Min = 96,  Max = 125 },
        new() { Code = "bar", Min = 126, Max = 155 },
        new() { Code = "dru", Min = 221, Max = 250 },
        new() { Code = "ass", Min = 251, Max = 280 },
        new() { Code = "war", Min = 373, Max = 402 }
    ];

    /// <summary>Canonical class index → 3-letter CharStats code (0..N-1).</summary>
    public static IReadOnlyList<string> ClassOrder { get; private set; } = DefaultClassOrder;

    /// <summary>Skill-id range buckets, used by <c>randclassskill</c> resolution.</summary>
    public static IReadOnlyList<ClassIdRange> IdRanges { get; private set; } = DefaultIdRanges;

    /// <summary>
    /// Load the JSON file. Missing file => keep defaults (so the tool still works
    /// without the config dropped on disk, matching <c>SyntheticStringRegistry</c>).
    /// </summary>
    public static async Task LoadAsync(string configDir)
    {
        var path = Path.Combine(configDir, "class-ranges.json");
        if (!File.Exists(path)) return;

        var json = await File.ReadAllTextAsync(path);
        var parsed = JsonSerializer.Deserialize<ClassRangeFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (parsed == null) return;

        if (parsed.ClassOrder is { Count: > 0 })
            ClassOrder = parsed.ClassOrder;

        if (parsed.IdRanges is { Count: > 0 })
            IdRanges = parsed.IdRanges;
    }

    /// <summary>
    /// Returns the 3-letter class code whose id range contains the given skill-id
    /// span (<paramref name="min"/>..<paramref name="max"/>), or <c>null</c> when
    /// no bucket matches. Uses the same inclusive-low / inclusive-high semantics
    /// the legacy resolver used.
    /// </summary>
    public static string? CodeForSkillRange(int? min, int? max)
    {
        if (!min.HasValue || !max.HasValue) return null;
        foreach (var r in IdRanges)
        {
            if (min.Value >= r.Min && max.Value <= r.Max) return r.Code;
        }
        return null;
    }

    /// <summary>
    /// Resolves a numeric class index (from a stat parameter) to its 3-letter
    /// CharStats code. Out-of-range indices return <c>null</c>.
    /// </summary>
    public static string? CodeForClassIndex(int index)
    {
        if (index < 0 || index >= ClassOrder.Count) return null;
        return ClassOrder[index];
    }

    private sealed class ClassRangeFile
    {
        [JsonPropertyName("classOrder")]
        public List<string>? ClassOrder { get; set; }

        [JsonPropertyName("idRanges")]
        public List<ClassIdRange>? IdRanges { get; set; }
    }
}

public sealed class ClassIdRange
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }
}
