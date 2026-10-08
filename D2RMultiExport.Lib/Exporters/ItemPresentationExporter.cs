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
    public const string OutputPath = "keyed/item-presentation.json";

    /// <summary>Game asset layout of the inventory panel background (structure, not content).</summary>
    private const string InventorySpriteSource = "hd/global/ui/panel/inventory/background_expanded2.sprite";

    private const string InventorySpriteOutput = "sprites/ui/inventory.webp";

    public const string Producer = "item-presentation.json";

    /// <summary>Asset manifests, relative to a data root (game layout, not content).</summary>
    internal const string ItemsManifest = "hd/items/items.json";
    internal const string UniquesManifest = "hd/items/uniques.json";
    internal const string SetsManifest = "hd/items/sets.json";

    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        GameData data,
        bool prettyPrint,
        AssetDiagnostics diagnostics,
        Action<string> warn,
        Action<string>? info = null)
    {
        // Each manifest is layered: the vanilla mapping first, the mod's entries on top, so a
        // mod that maps only its new items still gets vanilla art for the rest.
        var baseAssets = LoadLayered(sources, ItemsManifest, LoadBaseAssetMap, warn, info);
        var uniqueAssets = new NamedAssetMap(UniquesManifest,
            LoadLayered(sources, UniquesManifest, LoadNamedVariantAssetMap, warn, info));
        var setAssets = new NamedAssetMap(SetsManifest,
            LoadLayered(sources, SetsManifest, LoadNamedVariantAssetMap, warn, info));
        var sprites = new SpriteContext(outputs, sources, diagnostics, warn);

        var rows = new List<ItemPresentation>();
        foreach (var entry in data.Armors.Values.OrderBy(static item => item.Code, StringComparer.Ordinal))
        {
            rows.Add(await BuildEquipmentAsync(entry, "armor", baseAssets, uniqueAssets, setAssets, data, sprites));
        }
        foreach (var entry in data.Weapons.Values.OrderBy(static item => item.Code, StringComparer.Ordinal))
        {
            rows.Add(await BuildEquipmentAsync(entry, "weapon", baseAssets, uniqueAssets, setAssets, data, sprites));
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
                Sprite = await sprites.ExportBaseAsync("misc", entry.Code, asset)
            };
            foreach (var unique in data.Uniques.Where(item =>
                         string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
            {
                var variantAsset = GetVariant(uniqueAssets, unique.Index, "normal");
                result.UniqueSprites.Add(new ItemSpriteVariant
                {
                    FileIndex = unique.FileIndex,
                    NameKey = unique.Index,
                    Sprite = await sprites.ExportVariantAsync("misc", $"unique '{unique.Index}' ({entry.Code})",
                        variantAsset, result.Sprite)
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
                    Sprite = await sprites.ExportVariantAsync("misc", $"set item '{setItem.Index}' ({entry.Code})",
                        variantAsset, result.Sprite)
                });
            }
            rows.Add(result);
        }

        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        await outputs.WriteJsonAsync(OutputPath, rows, options);

        var inventorySource = sources.Resolve(InventorySpriteSource);
        if (inventorySource.Path is null)
            warn($"Inventory background omitted: '{InventorySpriteSource}' not found (searched {inventorySource.DescribeSearch()}).");
        else
            await sprites.ConvertOrWarnAsync("inventory background", inventorySource.Path, InventorySpriteOutput, 0);

        var exported = rows.Count(static r => r.Sprite is not null);
        info?.Invoke($"  {Producer}: {rows.Count} item(s), {exported} with a base sprite, "
            + $"{sprites.ConvertedCount} sprite file(s) converted ({sprites.SourceCounts}).");
        diagnostics.Summarize(Producer, warn);
    }

    /// <summary>
    /// Loads the vanilla and the mod copy of an asset manifest and overlays the mod's entries
    /// on the vanilla ones. Only when neither root provides the manifest is that reported.
    /// </summary>
    private static Dictionary<string, T> LoadLayered<T>(
        SourceRoots sources,
        string relativePath,
        Func<string, Action<string>, Dictionary<string, T>> load,
        Action<string> warn,
        Action<string>? info)
    {
        var vanilla = sources.ResolveIn(SourceOrigin.Casc, relativePath);
        var mod = sources.ResolveIn(SourceOrigin.Mod, relativePath);
        sources.Resolve(relativePath);
        if (!vanilla.Exists && !mod.Exists)
        {
            warn($"Sprites from '{relativePath}' omitted: not found (searched {string.Join(" ; ", mod.Searched.Concat(vanilla.Searched))}).");
            return new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        }
        var result = vanilla.Path is null ? new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase) : load(vanilla.Path, warn);
        var vanillaCount = result.Count;
        if (mod.Path is not null)
        {
            foreach (var (key, value) in load(mod.Path, warn)) result[key] = value;
        }
        info?.Invoke($"  {relativePath}: {result.Count} mapping(s) ({vanillaCount} vanilla, mod overlay {(mod.Exists ? "applied" : "absent")}).");
        return result;
    }

    private static async Task<ItemPresentation> BuildEquipmentAsync(
        EquipmentEntry entry,
        string category,
        IReadOnlyDictionary<string, string> baseAssets,
        NamedAssetMap uniqueAssets,
        NamedAssetMap setAssets,
        GameData data,
        SpriteContext sprites)
    {
        baseAssets.TryGetValue(entry.Code, out var baseAsset);
        var result = new ItemPresentation
        {
            Code = entry.Code,
            NameKey = entry.NameStr,
            Width = entry.InventoryWidth,
            Height = entry.InventoryHeight,
            Sprite = await sprites.ExportBaseAsync(category, entry.Code, baseAsset)
        };

        foreach (var unique in data.Uniques.Where(item =>
                     string.Equals(item.Code, entry.Code, StringComparison.OrdinalIgnoreCase)))
        {
            var asset = GetVariant(uniqueAssets, unique.Index, GetTier(entry, unique.Code));
            result.UniqueSprites.Add(new ItemSpriteVariant
            {
                FileIndex = unique.FileIndex,
                NameKey = unique.Index,
                Sprite = await sprites.ExportVariantAsync(category, $"unique '{unique.Index}' ({entry.Code})",
                    asset, result.Sprite)
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
                Sprite = await sprites.ExportVariantAsync(category, $"set item '{setItem.Index}' ({entry.Code})",
                    asset, result.Sprite)
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

    private static VariantLookup GetVariant(
        NamedAssetMap assets,
        string name,
        string tier)
    {
        var key = NormalizeAssetKey(name);
        var mapping = $"{assets.Manifest}#{key}";
        if (!assets.Exact.TryGetValue(key, out var entry))
        {
            var alias = NormalizeAssetAlias(name);
            if (!assets.Aliases.TryGetValue(alias, out var candidates))
            {
                return new VariantLookup(null, mapping, "no named artwork mapping (mod or CASC)");
            }
            if (candidates.Count != 1)
            {
                return new VariantLookup(null,
                    $"{mapping} (alias '{alias}'; candidates: {string.Join(", ", candidates.Order(StringComparer.Ordinal))})",
                    "ambiguous named artwork alias");
            }
            key = candidates.Single();
            entry = assets.Exact[key];
            mapping = $"{assets.Manifest}#{key}";
        }
        var asset = tier switch
        {
            "uber" => entry.Uber ?? entry.Normal,
            "ultra" => entry.Ultra ?? entry.Uber ?? entry.Normal,
            _ => entry.Normal
        };
        return new VariantLookup(asset, mapping,
            string.IsNullOrWhiteSpace(asset) ? $"named artwork mapping has no '{tier}' sprite" : null);
    }

    /// <summary>
    /// The HD asset manifests live outside <c>data\global\excel</c> and a mod tree does not
    /// have to ship them. A missing or malformed manifest costs sprites, not correctness, so
    /// it is reported and the sprites it would have supplied are omitted — the item rows are
    /// still exported. Individual malformed entries are reported and skipped.
    /// </summary>
    private static Dictionary<string, string> LoadBaseAssetMap(string path, Action<string> warn)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            warn($"Item sprites omitted: '{path}' is missing.");
            return result;
        }

        var skipped = 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                warn($"Item sprites omitted: '{path}' is not a JSON array.");
                return result;
            }
            foreach (var wrapper in document.RootElement.EnumerateArray())
            {
                if (wrapper.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                foreach (var property in wrapper.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("asset", out var asset)
                        && asset.ValueKind == JsonValueKind.String
                        && asset.GetString() is { Length: > 0 } value)
                    {
                        result[property.Name] = value;
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            warn($"Item sprites omitted: '{path}' is not valid JSON ({ex.Message}).");
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        if (skipped > 0)
            warn($"'{path}': {skipped} entr(ies) without a string \"asset\" were skipped.");
        return result;
    }

    private static Dictionary<string, AssetVariants> LoadNamedVariantAssetMap(string path, Action<string> warn)
    {
        var result = new Dictionary<string, AssetVariants>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            warn($"Unique/set sprite variants omitted: '{path}' is missing.");
            return result;
        }

        static string? Variant(JsonElement value, string name) =>
            value.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

        var skipped = 0;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                warn($"Unique/set sprite variants omitted: '{path}' is not a JSON array.");
                return result;
            }
            foreach (var wrapper in document.RootElement.EnumerateArray())
            {
                if (wrapper.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }
                var properties = wrapper.EnumerateObject();
                if (!properties.MoveNext() || properties.Current.Value.ValueKind != JsonValueKind.Object)
                {
                    skipped++;
                    continue;
                }
                var property = properties.Current;
                result[NormalizeAssetKey(property.Name)] = new AssetVariants(
                    Variant(property.Value, "normal"), Variant(property.Value, "uber"), Variant(property.Value, "ultra"), property.Name);
            }
        }
        catch (JsonException ex)
        {
            warn($"Unique/set sprite variants omitted: '{path}' is not valid JSON ({ex.Message}).");
            return new Dictionary<string, AssetVariants>(StringComparer.OrdinalIgnoreCase);
        }
        if (skipped > 0)
            warn($"'{path}': {skipped} malformed entr(ies) were skipped.");
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

    /// <summary>
    /// Compatibility aliases remove only apostrophes and the separator before a trailing
    /// numeric suffix. Other punctuation and distinct suffix numbers remain significant.
    /// </summary>
    private static string NormalizeAssetAlias(string value)
    {
        var withoutApostrophes = new string([.. value.Where(static character => character is not '\'' and not '\u2018' and not '\u2019')]);
        var key = NormalizeAssetKey(withoutApostrophes);
        var suffix = key.Length;
        while (suffix > 0 && char.IsAsciiDigit(key[suffix - 1]))
        {
            suffix--;
        }
        if (suffix > 1 && suffix < key.Length && key[suffix - 1] == '_' && char.IsLetter(key[suffix - 2]))
        {
            return key.Remove(suffix - 1, 1);
        }
        return key;
    }

    /// <summary>
    /// Canonicalizes an artwork-manifest path inside hd/global/ui/items, independently of
    /// the stricter data-root resolver. Only contained category-relative traversal is allowed.
    /// </summary>
    internal static string CanonicalizeAssetPath(string category, string asset)
    {
        var categorySegments = SourceRoots.SplitRelative(category);
        if (categorySegments.Length != 1 || string.IsNullOrWhiteSpace(asset)
            || asset.StartsWith('/') || asset.StartsWith('\\'))
        {
            throw new ArgumentException("Artwork paths must be non-empty and category-relative.", nameof(asset));
        }
        var segments = new List<string>();
        var parts = $"{categorySegments[0]}/{asset.Replace('\\', '/')}".Split('/');
        if (parts[^1] is "." or "..")
        {
            throw new ArgumentException("Artwork paths must end in an asset name.", nameof(asset));
        }
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException("Artwork path escapes the item-art root.", nameof(asset));
                }
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(static character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
            {
                throw new ArgumentException("Artwork path contains a malformed segment.", nameof(asset));
            }
            segments.Add(part);
        }
        return string.Join('/', segments).ToLowerInvariant();
    }

    private sealed class NamedAssetMap
    {
        public string Manifest { get; }
        public IReadOnlyDictionary<string, AssetVariants> Exact { get; }
        public Dictionary<string, HashSet<string>> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public NamedAssetMap(string manifest, IReadOnlyDictionary<string, AssetVariants> exact)
        {
            Manifest = manifest;
            Exact = exact;
            // Build aliases after layering: overridden and duplicate mappings retain last-wins semantics.
            foreach (var (key, entry) in exact)
            {
                var alias = NormalizeAssetAlias(entry.Name);
                if (!Aliases.TryGetValue(alias, out var candidates))
                {
                    candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Aliases.Add(alias, candidates);
                }
                candidates.Add(key);
            }
        }
    }

    private sealed record VariantLookup(string? Asset, string Mapping, string? Reason);

    /// <summary>
    /// Per-run sprite conversion state. A sprite is only ever referenced when this run
    /// converted it: an asset whose source is missing yields <c>null</c> (never a leftover
    /// <c>.webp</c> from an earlier export), and a corrupt sprite is reported and yields
    /// <c>null</c> rather than aborting every other item.
    /// </summary>
    private sealed class SpriteContext(
        ExportOutputs outputs,
        SourceRoots sources,
        AssetDiagnostics diagnostics,
        Action<string> warn)
    {
        private sealed record Conversion(string? Output, string? Source, string Searched, string? Reason);

        private readonly Dictionary<string, Conversion> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _outputSources = new(StringComparer.OrdinalIgnoreCase);
        private int _fromMod;
        private int _fromCasc;

        public int ConvertedCount => _fromMod + _fromCasc;

        public string SourceCounts => $"{_fromMod} from the mod, {_fromCasc} from CASC";

        /// <summary>The base art of an item code (<c>hd/items/items.json</c> mapping).</summary>
        public async Task<string?> ExportBaseAsync(string category, string code, string? asset)
        {
            if (string.IsNullOrWhiteSpace(asset))
            {
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, code, null, null, "",
                    $"no '{ItemsManifest}' mapping for the item code (mod or vanilla)", "none", AssetIssueKind.Missing));
                return null;
            }
            var conversion = await ConvertAsync(category, asset);
            if (conversion.Output is null)
            {
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, code, $"{category}/{asset}", conversion.Source,
                    conversion.Searched, conversion.Reason!, "none", AssetIssueKind.Missing));
            }
            return conversion.Output;
        }

        /// <summary>
        /// A unique/set variant. Without its own art (no named mapping, or the mapped sprite is
        /// unavailable) the base item's sprite is the supported fallback — the game draws the
        /// base item in that case too — and that substitution is recorded, not hidden.
        /// </summary>
        public async Task<string?> ExportVariantAsync(
            string category,
            string identifier,
            VariantLookup lookup,
            string? baseSprite)
        {
            if (lookup.Reason is not null)
            {
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, identifier, lookup.Mapping, null,
                    "", lookup.Reason, baseSprite is null ? "none" : $"base item sprite {baseSprite}",
                    baseSprite is null ? AssetIssueKind.Missing : AssetIssueKind.Fallback));
                return baseSprite;
            }

            // Shared artwork, including equivalent relative paths, hits the canonical conversion cache.
            var conversion = await ConvertAsync(category, lookup.Asset!);
            if (conversion.Output is not null) return conversion.Output;
            diagnostics.Add(new AssetDiagnostics.Entry(Producer, identifier, $"{lookup.Mapping}: {category}/{lookup.Asset}", conversion.Source,
                conversion.Searched, $"named variant art unavailable: {conversion.Reason}",
                baseSprite is null ? "none" : $"base item sprite {baseSprite}",
                baseSprite is null ? AssetIssueKind.Missing : AssetIssueKind.Fallback));
            return baseSprite;
        }

        private async Task<Conversion> ConvertAsync(string category, string asset)
        {
            string cacheKey;
            try
            {
                cacheKey = CanonicalizeAssetPath(category, asset);
            }
            catch (ArgumentException ex)
            {
                return new Conversion(null, null, "", $"invalid asset mapping ({ex.Message})");
            }
            if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

            var relativeOutput = $"sprites/items/{cacheKey.Replace('/', '-')}.webp";
            var relativeSource = $"hd/global/ui/items/{cacheKey}.sprite";

            var source = sources.Resolve(relativeSource, track: false);
            if (source.Path is null)
            {
                var reason = sources.CascDataRoot is null
                    ? "sprite file not in the mod and no CASC data root is configured"
                    : "sprite file exists in neither the mod nor the CASC data root";
                return _cache[cacheKey] = new Conversion(null, source.Expected, source.DescribeSearch(), reason);
            }

            if (_outputSources.TryGetValue(relativeOutput, out var claimedBy)
                && !string.Equals(claimedBy, source.Path, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new AssetDiagnostics.Entry(Producer, cacheKey, cacheKey, source.Path, claimedBy,
                    $"output '{relativeOutput}' is already produced from another source", "none", AssetIssueKind.Collision));
                return _cache[cacheKey] = new Conversion(null, source.Path, source.DescribeSearch(),
                    $"output file name collides with '{claimedBy}'");
            }

            if (!await ConvertOrWarnAsync(cacheKey, source.Path, relativeOutput, 0))
                return _cache[cacheKey] = new Conversion(null, source.Path, source.DescribeSearch(), "sprite could not be decoded");

            _outputSources[relativeOutput] = source.Path;
            if (source.Origin == SourceOrigin.Mod) _fromMod++;
            else _fromCasc++;
            return _cache[cacheKey] = new Conversion(relativeOutput, source.Path, source.DescribeSearch(), null);
        }

        public async Task<bool> ConvertOrWarnAsync(string identifier, string source, string relativeOutput, int frameIndex)
        {
            try
            {
                await ConvertSpriteAsync(outputs, source, relativeOutput, frameIndex);
                return true;
            }
            catch (InvalidDataException ex)
            {
                // Decoding fails before anything is written, so nothing this run owns needs
                // removing; an earlier run's copy is left alone, unreferenced and not in the manifest.
                warn($"Sprite for {identifier} omitted: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Converts frame <paramref name="frameIndex"/> of a D2R SpA1 sprite to WebP and writes it
    /// to <paramref name="relativeOutput"/> through <paramref name="outputs"/>. Throws
    /// <see cref="InvalidDataException"/> for a sprite it cannot decode.
    /// </summary>
    internal static async Task ConvertSpriteAsync(ExportOutputs outputs, string sourcePath, string relativeOutput, int frameIndex = 0)
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
        if (frameWidth > frameOffset || 40L + (long)totalWidth * height * 4 > bytes.Length)
        {
            throw new InvalidDataException($"'{sourcePath}' is truncated or its header dimensions do not match its pixel data.");
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
        await outputs.WriteAsync(relativeOutput, stream =>
        {
            encoded.SaveTo(stream);
            return Task.CompletedTask;
        });
    }


    private sealed record AssetVariants(string? Normal, string? Uber, string? Ultra, string Name);

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
