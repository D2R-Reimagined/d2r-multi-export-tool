// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports UnHoarder's literal selectors alongside localized catalog keys.</summary>
public static class LootFilterExporter
{
    public static async Task ExportAsync(string exportDir, string excelPath, MultiLanguageTranslationService translations, bool prettyPrint = true)
    {
        var weapons = await WeaponParser.GetEntries(Path.Combine(excelPath, "weapons.txt"));
        var armor = await ArmorParser.GetEntries(Path.Combine(excelPath, "armor.txt"));
        var misc = await MiscParser.GetEntries(Path.Combine(excelPath, "misc.txt"));
        var types = await ItemTypeParser.GetEntries(Path.Combine(excelPath, "itemtypes.txt"));
        var uniques = await UniqueItemsParser.GetEntries(Path.Combine(excelPath, "uniqueitems.txt"));
        var sets = await SetItemParser.GetEntries(Path.Combine(excelPath, "setitems.txt"));

        // The plugin's complete lookup tables include legacy/non-spawnable rows
        // omitted from the normal catalog. Seed their source names as English
        // fallbacks; real mod/CASC translations always win, including in other languages.
        var fallbackNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in weapons.Cast<Equipment>().Concat(armor).Concat(misc))
        {
            if (!ValidCode(row.Code) || string.IsNullOrWhiteSpace(row.Name)) continue;
            var key = string.IsNullOrWhiteSpace(row.NameStr) ? Clean(row.Code) : row.NameStr;
            fallbackNames.TryAdd(key, MultiLanguageTranslationService.StripCosmetic(row.Name));
        }
        foreach (var row in uniques.Where(row => ValidCode(row.Code) && !string.IsNullOrWhiteSpace(row.Index)))
            fallbackNames.TryAdd(row.Index!, row.Index!);
        foreach (var row in sets.Where(row => ValidCode(row.Item) && !string.IsNullOrWhiteSpace(row.Index)))
            fallbackNames.TryAdd(row.Index!, row.Index!);
        translations.MergeSynthetic(fallbackNames, new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal));

        // Literal names are protocol identifiers, not display text. Do not translate,
        // normalize, collapse type aliases, or apply catalog-only item exclusions here.
        static string Clean(string? value) => (value ?? "").TrimEnd(' ');
        static bool ValidCode(string? value)
        {
            var code = Clean(value);
            return code.Length is >= 1 and <= 4 && code.Any(c => c != ' ')
                && code.All(c => c is >= ' ' and <= '~');
        }
        static object[] Bases(IEnumerable<Equipment> rows, string kind) => rows
            .Where(row => ValidCode(row.Code))
            .Select(row => (object)new
            {
                Kind = kind,
                Code = Clean(row.Code),
                NameKey = string.IsNullOrWhiteSpace(row.NameStr) ? Clean(row.Code) : row.NameStr,
                BaseNameSelector = kind == "misc" ? "" : row.Name ?? "",
                TypeCode = Clean(row.Type),
                TypeCode2 = Clean(row.Type2)
            }).ToArray();

        var bundle = new
        {
            SchemaVersion = 1,
            FilterVersion = 3,
            BaseItems = Bases(weapons, "weapon").Concat(Bases(armor, "armor")).Concat(Bases(misc, "misc")),
            ItemTypes = types.Where(row => !string.IsNullOrWhiteSpace(row.Code)).Select(row => new
            {
                Code = Clean(row.Code),
                TypeNameSelector = row.ItemTypeName ?? "",
                ParentCode = Clean(row.Equiv1),
                ParentCode2 = Clean(row.Equiv2)
            }),
            UniqueItems = uniques.Where(row => ValidCode(row.Code) && !string.IsNullOrWhiteSpace(row.Index))
                .Select(row => new { NameKey = row.Index, Code = Clean(row.Code) }),
            SetItems = sets.Where(row => ValidCode(row.Item) && !string.IsNullOrWhiteSpace(row.Index))
                .Select(row => new { NameKey = row.Index, Code = Clean(row.Item) })
        };
        var directory = Path.Combine(exportDir, "keyed");
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(Path.Combine(directory, "loot-filter.json"));
        await JsonSerializer.SerializeAsync(stream, bundle, new JsonSerializerOptions { WriteIndented = prettyPrint });
    }
}
