// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2RMultiExport.Lib.Models;
using SkiaSharp;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Exports the inventory-grid dimensions and HD item sprites required by the
/// website character viewer. Reimagined assets are overlaid on an optional
/// extracted vanilla data tree and converted from D2R's SpA1 format to WebP.
/// </summary>
public static class ItemPresentationExporter
{
    public static async Task ExportAsync(
        string exportDir,
        string excelPath,
        string? baseAssetsPath,
        GameData data,
        bool prettyPrint = true)
    {
        var modDataRoot = Directory.GetParent(excelPath)?.Parent?.FullName
            ?? throw new DirectoryNotFoundException($"Could not resolve the mod data root from '{excelPath}'.");
        var assetRoots = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseAssetsPath))
        {
            assetRoots.Add(NormalizeDataRoot(baseAssetsPath));
        }
        assetRoots.Add(modDataRoot);

        var baseAssets = LoadBaseAssetMap(Path.Combine(modDataRoot, "hd", "items", "items.json"));
        var uniqueAssets = LoadNamedVariantAssetMap(Path.Combine(modDataRoot, "hd", "items", "uniques.json"));
        var setAssets = LoadNamedVariantAssetMap(Path.Combine(modDataRoot, "hd", "items", "sets.json"));
        var spriteCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var rows = new List<ItemPresentation>();
        foreach (var entry in data.Armors.Values.OrderBy(static item => item.Code, StringComparer.Ordinal))
        {
            rows.Add(await BuildEquipmentAsync(entry, "armor", baseAssets, uniqueAssets, setAssets,
                data, assetRoots, exportDir, spriteCache));
        }
        foreach (var entry in data.Weapons.Values.OrderBy(static item => item.Code, StringComparer.Ordinal))
        {
            rows.Add(await BuildEquipmentAsync(entry, "weapon", baseAssets, uniqueAssets, setAssets,
                data, assetRoots, exportDir, spriteCache));
        }
        foreach (var entry in data.MiscItems.Values.OrderBy(static item => item.Code, StringComparer.Ordinal))
        {
            baseAssets.TryGetValue(entry.Code, out var asset);
            var result = new ItemPresentation
            {
                Code = entry.Code,
                NameKey = entry.NameStr,
                Width = entry.InventoryWidth,
                Height = entry.InventoryHeight,
                Sprite = await ExportSpriteAsync("misc", asset, assetRoots, exportDir, spriteCache)
            };
            foreach (var unique in data.Uniques.Where(item =>
                         string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
            {
                var variantAsset = GetVariant(uniqueAssets, unique.Index, "normal");
                result.UniqueSprites.Add(new ItemSpriteVariant
                {
                    FileIndex = unique.FileIndex,
                    NameKey = unique.Index,
                    Sprite = await ExportSpriteAsync("misc", variantAsset ?? asset, assetRoots, exportDir, spriteCache)
                });
            }
            foreach (var setItem in data.Sets.SelectMany(static set => set.SetItems).Where(item =>
                         string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
            {
                var variantAsset = GetVariant(setAssets, setItem.Index, "normal");
                result.SetSprites.Add(new ItemSpriteVariant
                {
                    FileIndex = setItem.FileIndex,
                    NameKey = setItem.Index,
                    Sprite = await ExportSpriteAsync("misc", variantAsset ?? asset, assetRoots, exportDir, spriteCache)
                });
            }
            rows.Add(result);
        }

        var keyedDir = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(keyedDir);
        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await using (var stream = File.Create(Path.Combine(keyedDir, "item-presentation.json")))
        {
            await JsonSerializer.SerializeAsync(stream, rows, options);
        }

        await ExportUiSpriteAsync(
            Path.Combine("hd", "global", "ui", "panel", "inventory", "background_expanded2.sprite"),
            Path.Combine(exportDir, "sprites", "ui", "inventory.webp"),
            assetRoots);
    }

    private static async Task<ItemPresentation> BuildEquipmentAsync(
        EquipmentEntry entry,
        string category,
        IReadOnlyDictionary<string, string> baseAssets,
        IReadOnlyDictionary<string, AssetVariants> uniqueAssets,
        IReadOnlyDictionary<string, AssetVariants> setAssets,
        GameData data,
        IReadOnlyList<string> assetRoots,
        string exportDir,
        IDictionary<string, string?> spriteCache)
    {
        baseAssets.TryGetValue(entry.Code, out var baseAsset);
        var result = new ItemPresentation
        {
            Code = entry.Code,
            NameKey = entry.NameStr,
            Width = entry.InventoryWidth,
            Height = entry.InventoryHeight,
            Sprite = await ExportSpriteAsync(category, baseAsset, assetRoots, exportDir, spriteCache)
        };

        foreach (var unique in data.Uniques.Where(item =>
                     string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
        {
            var asset = GetVariant(uniqueAssets, unique.Index, GetTier(entry, unique.Code));
            result.UniqueSprites.Add(new ItemSpriteVariant
            {
                FileIndex = unique.FileIndex,
                NameKey = unique.Index,
                Sprite = await ExportSpriteAsync(category, asset ?? baseAsset, assetRoots, exportDir, spriteCache)
            });
        }

        foreach (var setItem in data.Sets.SelectMany(static set => set.SetItems).Where(item =>
                     string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
        {
            var asset = GetVariant(setAssets, setItem.Index, GetTier(entry, setItem.Code));
            result.SetSprites.Add(new ItemSpriteVariant
            {
                FileIndex = setItem.FileIndex,
                NameKey = setItem.Index,
                Sprite = await ExportSpriteAsync(category, asset ?? baseAsset, assetRoots, exportDir, spriteCache)
            });
        }

        return result;
    }

    private static string GetTier(EquipmentEntry entry, string code)
    {
        if (string.Equals(entry.UltraCode, code, StringComparison.OrdinalIgnoreCase)) return "ultra";
        if (string.Equals(entry.UberCode, code, StringComparison.OrdinalIgnoreCase)) return "uber";
        return "normal";
    }

    private static string? GetVariant(
        IReadOnlyDictionary<string, AssetVariants> assets,
        string name,
        string tier)
    {
        if (!assets.TryGetValue(NormalizeAssetKey(name), out var entry)) return null;
        return tier switch
        {
            "uber" => entry.Uber ?? entry.Normal,
            "ultra" => entry.Ultra ?? entry.Uber ?? entry.Normal,
            _ => entry.Normal
        };
    }

    private static Dictionary<string, string> LoadBaseAssetMap(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wrapper in document.RootElement.EnumerateArray())
        {
            foreach (var property in wrapper.EnumerateObject())
            {
                if (property.Value.TryGetProperty("asset", out var asset) && asset.GetString() is { Length: > 0 } value)
                {
                    result[property.Name] = value;
                }
            }
        }
        return result;
    }

    private static Dictionary<string, AssetVariants> LoadNamedVariantAssetMap(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, AssetVariants>(StringComparer.OrdinalIgnoreCase);
        foreach (var wrapper in document.RootElement.EnumerateArray())
        {
            var property = wrapper.EnumerateObject().First();
            result[NormalizeAssetKey(property.Name)] = new AssetVariants(
                property.Value.TryGetProperty("normal", out var normal) ? normal.GetString() : null,
                property.Value.TryGetProperty("uber", out var uber) ? uber.GetString() : null,
                property.Value.TryGetProperty("ultra", out var ultra) ? ultra.GetString() : null);
        }
        return result;
    }

    private static string NormalizeAssetKey(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        var needsSeparator = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (needsSeparator && result.Length > 0) result.Append('_');
                result.Append(char.ToLowerInvariant(character));
                needsSeparator = false;
            }
            else
            {
                needsSeparator = true;
            }
        }
        return result.ToString();
    }

    private static async Task<string?> ExportSpriteAsync(
        string category,
        string? asset,
        IReadOnlyList<string> assetRoots,
        string exportDir,
        IDictionary<string, string?> cache)
    {
        if (string.IsNullOrWhiteSpace(asset)) return null;
        var cacheKey = $"{category}/{asset}";
        if (cache.TryGetValue(cacheKey, out var cached)) return cached;

        var fileName = string.Join('-', new[] { category, asset }
            .SelectMany(static part => part.Split('/', '\\'))).ToLowerInvariant() + ".webp";
        var relativeOutput = Path.Combine("sprites", "items", fileName).Replace('\\', '/');
        var outputPath = Path.Combine(exportDir, relativeOutput.Replace('/', Path.DirectorySeparatorChar));
        var relativeSource = Path.Combine("hd", "global", "ui", "items", category,
            asset.Replace('/', Path.DirectorySeparatorChar) + ".sprite");
        var source = assetRoots.Select(root => Path.Combine(root, relativeSource)).LastOrDefault(File.Exists);
        if (source is null)
        {
            if (File.Exists(outputPath))
            {
                cache[cacheKey] = relativeOutput;
                return relativeOutput;
            }
            cache[cacheKey] = null;
            return null;
        }

        await ConvertSpriteAsync(source, outputPath);
        cache[cacheKey] = relativeOutput;
        return relativeOutput;
    }

    private static async Task ExportUiSpriteAsync(
        string relativeSource,
        string outputPath,
        IReadOnlyList<string> assetRoots)
    {
        var source = assetRoots.Select(root => Path.Combine(root, relativeSource)).LastOrDefault(File.Exists);
        if (source is not null)
        {
            await ConvertSpriteAsync(source, outputPath);
        }
    }

    internal static async Task ConvertSpriteAsync(string sourcePath, string outputPath, int frameIndex = 0)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        if (bytes.Length < 40 || bytes[0] != (byte)'S' || bytes[1] != (byte)'p'
                              || bytes[2] != (byte)'A' || bytes[3] != (byte)'1')
        {
            throw new InvalidDataException($"'{sourcePath}' is not a supported SpA1 sprite.");
        }

        var version = BitConverter.ToUInt16(bytes, 4);
        if (version != 31)
        {
            throw new InvalidDataException($"'{sourcePath}' uses unsupported sprite version {version}.");
        }

        var frameWidth = BitConverter.ToUInt16(bytes, 6);
        var totalWidth = checked((int)BitConverter.ToUInt32(bytes, 8));
        var height = checked((int)BitConverter.ToUInt32(bytes, 12));
        var frames = checked((int)BitConverter.ToUInt32(bytes, 20));
        if (frameWidth <= 0 || totalWidth <= 0 || height <= 0 || frames <= 0)
        {
            throw new InvalidDataException($"'{sourcePath}' has invalid sprite dimensions.");
        }

        var frameOffset = totalWidth / frames;
        if (frameIndex < 0 || frameIndex >= frames)
        {
            throw new InvalidDataException($"'{sourcePath}' does not contain frame {frameIndex} of {frames}.");
        }
        var pixels = new byte[checked(frameWidth * height * 4)];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(
                bytes,
                40 + ((y * totalWidth + frameIndex * frameOffset) * 4),
                pixels,
                y * frameWidth * 4,
                frameWidth * 4);
        }

        using var bitmap = new SKBitmap(new SKImageInfo(
            frameWidth,
            height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 92)
            ?? throw new InvalidOperationException($"Could not encode '{sourcePath}' as WebP.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await using var output = File.Create(outputPath);
        encoded.SaveTo(output);
    }

    internal static string NormalizeDataRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(Path.Combine(fullPath, "data", "hd"))) return Path.Combine(fullPath, "data");
        return fullPath;
    }

    private sealed record AssetVariants(string? Normal, string? Uber, string? Ultra);

    private sealed class ItemPresentation
    {
        public string Code { get; init; } = "";
        public string NameKey { get; init; } = "";
        public int Width { get; init; }
        public int Height { get; init; }
        public string? Sprite { get; init; }
        public List<ItemSpriteVariant> UniqueSprites { get; } = [];
        public List<ItemSpriteVariant> SetSprites { get; } = [];
    }

    private sealed class ItemSpriteVariant
    {
        public int FileIndex { get; init; }
        public string NameKey { get; init; } = "";
        public string? Sprite { get; init; }
    }
}
