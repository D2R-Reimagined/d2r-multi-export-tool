// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Manifest-verified English strings used only while rendering a recipe comparison.</summary>
internal sealed class RecipeTranslations
{
    private RecipeTranslations(Dictionary<string, string> values, string status)
    {
        Values = values;
        Status = status;
    }

    public Dictionary<string, string> Values { get; }

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
            return new RecipeTranslations(values, status);
        }
        catch (JsonException ex)
        {
            return Unavailable($"strings/enUS.json is invalid JSON ({ex.Message})");
        }
    }

    private static RecipeTranslations Unavailable(string reason) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal), $"unavailable: {reason}");
}
