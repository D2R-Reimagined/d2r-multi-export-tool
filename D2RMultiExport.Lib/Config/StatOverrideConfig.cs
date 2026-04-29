// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RMultiExport.Lib.Config;

public sealed class StatOverrideConfig
{
    [JsonPropertyName("statOverrides")]
    public List<StatOverrideEntry> StatOverrides { get; set; } = [];

    [JsonPropertyName("statFixes")]
    public List<StatOverrideEntry> StatFixes { get; set; } = [];

    public static async Task<StatOverrideConfig> LoadAsync(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<StatOverrideConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? new StatOverrideConfig();
    }
}

public sealed class StatOverrideEntry
{
    [JsonPropertyName("stat")]
    public string Stat { get; set; } = "";

    [JsonPropertyName("descPriority")]
    public int? DescPriority { get; set; }

    [JsonPropertyName("descPriorityFromStat")]
    public string? DescPriorityFromStat { get; set; }

    [JsonPropertyName("descFunc")]
    public int? DescFunc { get; set; }

    [JsonPropertyName("descVal")]
    public int? DescVal { get; set; }

    [JsonPropertyName("descStrPosKey")]
    public string? DescStrPosKey { get; set; }

    [JsonPropertyName("descStrNegKey")]
    public string? DescStrNegKey { get; set; }

    [JsonPropertyName("descStrPosKeyFromStat")]
    public string? DescStrPosKeyFromStat { get; set; }

    [JsonPropertyName("descStrNegKeyFromStat")]
    public string? DescStrNegKeyFromStat { get; set; }
}
