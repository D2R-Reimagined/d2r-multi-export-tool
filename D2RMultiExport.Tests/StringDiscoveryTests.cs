// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

/// <summary>
/// String files are discovered only directly in each resolved <c>local/lng/strings</c>
/// directory, in a fixed order, and keep the mod-first / CASC-fallback precedence.
/// </summary>
public sealed class StringDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-strings-").FullName;

    private string ModStrings => Path.Combine(_root, "mod", "data", "local", "lng", "strings");
    private string CascStrings => Path.Combine(_root, "casc", "data", "data", "local", "lng", "strings");

    public StringDiscoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "mod", "data", "global", "excel"));
        Directory.CreateDirectory(ModStrings);
        Directory.CreateDirectory(CascStrings);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void WriteStrings(string path, params (string Key, string EnUS)[] entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var rows = entries.Select((e, i) => new Dictionary<string, object> { ["id"] = i + 1, ["Key"] = e.Key, ["enUS"] = e.EnUS, ["deDE"] = e.EnUS + " (de)" });
        File.WriteAllText(path, JsonSerializer.Serialize(rows));
    }

    [Fact]
    public void OnlyTopLevelJsonFilesAreListedInFileNameOrder()
    {
        WriteStrings(Path.Combine(ModStrings, "skills.json"));
        WriteStrings(Path.Combine(ModStrings, "Item-Names.json"));
        WriteStrings(Path.Combine(ModStrings, "item-modifiers.json"));
        File.WriteAllText(Path.Combine(ModStrings, "notes.txt"), "");
        WriteStrings(Path.Combine(ModStrings, "backup", "item-names.json"));
        WriteStrings(Path.Combine(ModStrings, "metadata", "metadata-states.json"));

        var files = SourceRoots.StringFiles(ModStrings).Select(Path.GetFileName);

        Assert.Equal(["item-modifiers.json", "Item-Names.json", "skills.json"], files);
        Assert.Empty(SourceRoots.StringFiles(Path.Combine(_root, "absent")));
        Assert.Empty(SourceRoots.StringFiles(null));
    }

    [Fact]
    public async Task NestedFilesNeverOverrideOrAddTranslations()
    {
        WriteStrings(Path.Combine(ModStrings, "item-names.json"), ("strShipped", "Shipped"));
        // A backup copy below the strings folder: recursive discovery used to load it and,
        // depending on enumeration order, let it replace the shipped text.
        WriteStrings(Path.Combine(ModStrings, "zz-backup", "item-names.json"), ("strShipped", "Stale backup"), ("strBackupOnly", "Backup only"));
        WriteStrings(Path.Combine(CascStrings, "ui.json"), ("strVanilla", "Vanilla"));
        WriteStrings(Path.Combine(CascStrings, "metadata", "metadata-extra.json"), ("strMetadataOnly", "Metadata"));

        var translations = new MultiLanguageTranslationService();
        await translations.LoadFromDirectoryAsync(ModStrings);
        await translations.LoadFallbackFromDirectoryAsync(CascStrings);

        Assert.True(translations.TryGetForLanguage("enUS", "strShipped", out var shipped));
        Assert.Equal("Shipped", shipped);
        Assert.False(translations.TryGetForLanguage("enUS", "strBackupOnly", out _));
        Assert.True(translations.TryGetForLanguage("enUS", "strVanilla", out _));
        Assert.False(translations.ContainsKeyOrFallback("strMetadataOnly"));

        var legacy = new TranslationService();
        await legacy.LoadD2RTranslationsAsync(SourceRoots.StringFiles(ModStrings));
        Assert.Equal("Shipped", legacy.GetValue("strShipped"));
        Assert.False(legacy.ContainsKey("strBackupOnly"));
    }

    [Fact]
    public async Task ModStringsStillOverrideCascFallbackAndLaterFilesWinWithinARoot()
    {
        WriteStrings(Path.Combine(ModStrings, "item-modifiers.json"), ("strShared", "Mod first file"), ("strModOnly", "Mod"));
        WriteStrings(Path.Combine(ModStrings, "ui.json"), ("strShared", "Mod later file"));
        WriteStrings(Path.Combine(CascStrings, "item-modifiers.json"), ("strShared", "Vanilla"), ("strCascOnly", "Vanilla only"));

        var translations = new MultiLanguageTranslationService();
        await translations.LoadFromDirectoryAsync(ModStrings);
        await translations.LoadFallbackFromDirectoryAsync(CascStrings);
        translations.ApplyFallback(new HashSet<string>(["strShared", "strModOnly", "strCascOnly"], StringComparer.Ordinal));

        var enUS = translations.ForLanguage("enUS");
        Assert.Equal("Mod later file", enUS["strShared"]);
        Assert.Equal("Mod", enUS["strModOnly"]);
        Assert.Equal("Vanilla only", enUS["strCascOnly"]);
        Assert.Equal("Mod later file (de)", translations.ForLanguage("deDE")["strShared"]);
        Assert.Equal(["strCascOnly"], translations.LastFallbackPromotions.Keys);
    }

    [Fact]
    public void ResolvedModStringsDirectoryIsTheFixedLayoutLocation()
    {
        var sources = SourceRoots.Create(Path.Combine(_root, "mod"), Path.Combine(_root, "casc"));

        Assert.Equal(ModStrings, sources.ModStringsDirectory);
        Assert.Equal(CascStrings, sources.CascStringsDirectory);
    }
}
