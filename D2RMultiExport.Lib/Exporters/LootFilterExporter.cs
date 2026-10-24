// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Translation;
using D2RReimaginedTools.Models;
using D2RReimaginedTools.TextFileParsers;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Exports UnHoarder's literal selectors alongside localized catalog keys.</summary>
public static class LootFilterExporter
{
    public const string OutputPath = "keyed/loot-filter.json";

    private static readonly string[] InputFiles =
        ["weapons.txt", "armor.txt", "misc.txt", "itemtypes.txt", "uniqueitems.txt", "setitems.txt"];

    public static async Task ExportAsync(
        ExportOutputs outputs,
        SourceRoots sources,
        MultiLanguageTranslationService translations,
        bool prettyPrint,
        Action<string> warn)
    {
        if (!ExcelInputs.RequireAll(sources, OutputPath, warn, InputFiles)) return;

        var weapons = await WeaponParser.GetEntries(sources.Excel("weapons.txt"));
        var armor = await ArmorParser.GetEntries(sources.Excel("armor.txt"));
        var misc = await MiscParser.GetEntries(sources.Excel("misc.txt"));
        var types = await ItemTypeParser.GetEntries(sources.Excel("itemtypes.txt"));
        var uniques = await UniqueItemsParser.GetEntries(sources.Excel("uniqueitems.txt"));
        var sets = await SetItemParser.GetEntries(sources.Excel("setitems.txt"));

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
        await outputs.WriteJsonAsync(OutputPath, bundle, new JsonSerializerOptions { WriteIndented = prettyPrint });
    }
}
