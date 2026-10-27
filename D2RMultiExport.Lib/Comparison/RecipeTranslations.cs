// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Manifest-verified English strings used only while rendering comparison Markdown.</summary>
internal sealed class RecipeTranslations
{
    private const string ItemStatPresentationPath = "keyed/item-stat-presentation.json";
    private const string ItemStatPresentationProducer = "item-stat-presentation.json";

    /// <summary>ItemStatCost key columns that can reference a localized stat description.</summary>
    private static readonly string[] PresentationKeyFields = ["PositiveKey", "NegativeKey", "SecondaryKey"];

    /// <summary>
    /// The ethereal marker uses a fixed keyed display override rather than an ItemStatCost row.
    /// </summary>
    private static readonly HashSet<string> VerifiedFlagKeyExceptions = new(StringComparer.Ordinal)
    {
        "strethereal"
    };

    private RecipeTranslations(
        Dictionary<string, string> values,
        HashSet<string> verifiedFlagKeys,
        string status)
    {
        Values = values;
        VerifiedFlagKeys = verifiedFlagKeys;
        Status = status;
    }

    public Dictionary<string, string> Values { get; }

    public IReadOnlySet<string> VerifiedFlagKeys { get; }

    public string Status { get; }

    public bool TryGet(string key, out string value) => Values.TryGetValue(key, out value!);

    public static async Task<RecipeTranslations> LoadAsync(SnapshotInfo expectedSnapshot)
    {
        if (string.IsNullOrWhiteSpace(expectedSnapshot.ManifestSha256))
            return Unavailable("the comparison did not record a manifest hash");

        SnapshotReader reader;
        try
        {
            reader = await SnapshotReader.LoadAsync(expectedSnapshot.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Unavailable($"the snapshot could not be revalidated ({ex.Message})");
        }

        if (reader.Info.Errors.Count > 0)
            return Unavailable("the current snapshot manifest failed validation");

        if (!string.Equals(reader.Info.ManifestSha256, expectedSnapshot.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            return Unavailable("the manifest changed since comparison");

        var file = await reader.ReadFileAsync("strings/enUS.json", "strings");
        if (file.Payload is null)
        {
            var detail = file.Errors.Count > 0
                ? string.Join("; ", file.Errors.Select(static issue => issue.Message))
                : "the file is not a verified current-run strings output";
            return Unavailable(detail);
        }

        if (file.Payload.Sha256 is null || file.Warnings.Count > 0)
            return Unavailable("strings/enUS.json is missing manifest integrity metadata");

        try
        {
            using var document = JsonDocument.Parse(file.Payload.Bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return Unavailable("strings/enUS.json is not a JSON object");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return Unavailable($"strings/enUS.json contains a non-string value for '{property.Name}'");
                if (!values.TryAdd(property.Name, property.Value.GetString() ?? ""))
                    return Unavailable($"strings/enUS.json contains duplicate key '{property.Name}'");
            }

            var status = reader.Info.Warnings.Count == 0
                ? "available (manifest-verified)"
                : "available (manifest-verified; snapshot has warnings)";
            var verifiedFlagKeys = await LoadVerifiedFlagKeysAsync(reader, values);
            return new RecipeTranslations(values, verifiedFlagKeys, status);
        }
        catch (JsonException ex)
        {
            return Unavailable($"strings/enUS.json is invalid JSON ({ex.Message})");
        }
    }

    private static async Task<HashSet<string>> LoadVerifiedFlagKeysAsync(
        SnapshotReader reader,
        IReadOnlyDictionary<string, string> translations)
    {
        var verifiedKeys = new HashSet<string>(VerifiedFlagKeyExceptions, StringComparer.Ordinal);
        var file = await reader.ReadFileAsync(ItemStatPresentationPath, ItemStatPresentationProducer);
        if (file.Payload is null || file.Payload.Sha256 is null || file.Errors.Count > 0 || file.Warnings.Count > 0)
            return verifiedKeys;

        try
        {
            using var document = JsonDocument.Parse(file.Payload.Bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Stats", out var stats) ||
                stats.ValueKind != JsonValueKind.Object)
                return verifiedKeys;

            var references = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var stat in stats.EnumerateObject())
            {
                if (stat.Value.ValueKind != JsonValueKind.Object) continue;
                var isFixedFunction = stat.Value.TryGetProperty("Function", out var function) &&
                                      function.ValueKind == JsonValueKind.Number &&
                                      function.TryGetInt32(out var functionNumber) && functionNumber == 19;
                foreach (var field in PresentationKeyFields)
                {
                    if (!stat.Value.TryGetProperty(field, out var keyValue) ||
                        keyValue.ValueKind != JsonValueKind.String ||
                        string.IsNullOrEmpty(keyValue.GetString()))
                        continue;

                    var key = keyValue.GetString()!;
                    if (references.TryGetValue(key, out var existing))
                        references[key] = existing && isFixedFunction;
                    else
                        references.Add(key, isFixedFunction);
                }
            }

            foreach (var (key, isFixedFunction) in references)
            {
                if (isFixedFunction && translations.TryGetValue(key, out var template) &&
                    !string.IsNullOrWhiteSpace(template) &&
                    !template.Contains('%'))
                    verifiedKeys.Add(key);
            }
        }
        catch (JsonException)
        {
            // This optional presentation file cannot justify suppressing any warning if malformed.
        }

        return verifiedKeys;
    }

    private static RecipeTranslations Unavailable(string reason) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(VerifiedFlagKeyExceptions, StringComparer.Ordinal), $"unavailable: {reason}");
}
