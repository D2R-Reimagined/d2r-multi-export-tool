// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Config;

/// <summary>
/// How one <c>skilldesc.txt</c> description function binds its sibling
/// <c>desctextA</c>/<c>desctextB</c>/<c>desccalcA</c>/<c>desccalcB</c> columns to the
/// translated template. Loaded from <c>Config/skill-desc-functions.json</c> so a mod can
/// teach the exporter a new description function without a recompile.
/// </summary>
public static class SkillDescFunctionConfig
{
    /// <summary>
    /// Fallback table used when no JSON file is present, mirroring the description
    /// functions D2R Reimagined currently ships in <c>skilldesc.txt</c>.
    /// </summary>
    private static readonly Dictionary<int, SkillDescFunction> DefaultFunctions = new()
    {
        [12] = new() { Layout = "valueWithDivisor" },
        [13] = new() { Layout = "sumOfValues" },
        [18] = new() { Layout = "textOnly" },
        [31] = new() { Layout = "valueWithDivisor", Plural = true },
        [36] = new() { Layout = "valueWithDivisor", Plural = true },
        [40] = new() { Layout = "textOnly", NameArg = true },
        [56] = new() { Layout = "textOnly" },
        [74] = new() { Layout = "valueWithDivisor" },
        [75] = new() { Layout = "twoValues" },
        [76] = new() { Layout = "singleValue", NameArg = true },
        [77] = new() { Layout = "twoValues", NameArg = true },
        [78] = new() { Layout = "textOnly", NameArg = true },
        [79] = new() { Layout = "valueWithDivisor", NameArg = true }
    };

    /// <summary>Description function id (the <c>descline</c> column value) → layout.</summary>
    public static IReadOnlyDictionary<int, SkillDescFunction> Functions { get; private set; } = DefaultFunctions;

    /// <summary>
    /// Load the JSON file. Missing file => keep defaults, matching
    /// <see cref="ClassRangeConfig"/>.
    /// </summary>
    public static async Task LoadAsync(string configDir)
    {
        var path = Path.Combine(configDir, "skill-desc-functions.json");
        if (!File.Exists(path)) return;

        var json = await File.ReadAllTextAsync(path);
        var parsed = JsonSerializer.Deserialize<SkillDescFunctionFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (parsed?.Functions is not { Count: > 0 }) return;

        var table = new Dictionary<int, SkillDescFunction>();
        foreach (var (key, value) in parsed.Functions)
        {
            if (int.TryParse(key, out var id)) table[id] = value;
        }
        if (table.Count > 0) Functions = table;
    }

    /// <summary>Returns the layout for a description function id, or <c>null</c> when unmapped.</summary>
    public static SkillDescFunction? Resolve(int function)
        => Functions.TryGetValue(function, out var layout) ? layout : null;

    private sealed class SkillDescFunctionFile
    {
        [JsonPropertyName("functions")]
        public Dictionary<string, SkillDescFunction>? Functions { get; set; }
    }
}

/// <summary>One row of the description-function layout table.</summary>
public sealed class SkillDescFunction
{
    /// <summary>
    /// One of <c>textOnly</c>, <c>singleValue</c>, <c>valueWithDivisor</c>,
    /// <c>twoValues</c>, <c>sumOfValues</c>. See <c>$layoutNote</c> in the JSON file.
    /// </summary>
    [JsonPropertyName("layout")]
    public string Layout { get; set; } = "textOnly";

    /// <summary>desctextA is the singular template, desctextB the plural one.</summary>
    [JsonPropertyName("plural")]
    public bool Plural { get; set; }

    /// <summary>desctextB is a translation key spliced in as the template's leading <c>%s</c>.</summary>
    [JsonPropertyName("nameArg")]
    public bool NameArg { get; set; }
}
