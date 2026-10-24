// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;

namespace D2RMultiExport.Tests;

/// <summary>
/// Runs its tests alone because they change the process-wide current directory: a child
/// process another test starts meanwhile (the <c>mklink</c> junction helpers) inherits that
/// directory and holds it open, so the temporary root could not be deleted.
/// </summary>
[CollectionDefinition(nameof(ProcessWorkingDirectoryCollection), DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
}

/// <summary>Layout recognition, precedence and diagnostics of the two source roots.</summary>
[Collection(nameof(ProcessWorkingDirectoryCollection))]
public sealed class SourceRootsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-sources-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string MakeDataRoot(string relative, bool excel = true, bool strings = true, bool hd = false)
    {
        var data = Path.Combine(_root, relative);
        if (excel) Directory.CreateDirectory(Path.Combine(data, "global", "excel"));
        if (strings) Directory.CreateDirectory(Path.Combine(data, "local", "lng", "strings"));
        if (hd) Directory.CreateDirectory(Path.Combine(data, "hd", "global"));
        Directory.CreateDirectory(data);
        return data;
    }

    private static void Write(string path, string text = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ModRootIsAcceptedAsDataOrAsTheFolderContainingIt()
    {
        var data = MakeDataRoot(Path.Combine("mod", "data"));

        Assert.Equal(data, SourceRoots.ValidateModRoot(data).Root);
        Assert.Equal(data, SourceRoots.ValidateModRoot(Path.Combine(_root, "mod")).Root);
        Assert.Equal(data, SourceRoots.ValidateModRoot(data + Path.DirectorySeparatorChar).Root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModRootsWithoutStringsAreRejectedBeforeExecution(bool selectParent)
    {
        var mod = MakeDataRoot(Path.Combine("mod", "data"), strings: false);
        var selected = selectParent ? Path.GetDirectoryName(mod)! : mod;
        var validation = SourceRoots.ValidateModRoot(selected);
        Assert.Throws<SourceLayoutException>(() =>
            new D2RMultiExportPipeline(selected, null, Path.Combine(_root, "out")));
        Assert.False(validation.IsValid);
        Assert.Contains("'local/lng/strings'", validation.Message);
        Assert.Throws<SourceLayoutException>(() => SourceRoots.Create(selected, null));
    }

    [Theory]
    [InlineData("casc-flat")]       // the selection is the data root
    [InlineData("casc-nested")]     // CascView: <selection>/data/data
    [InlineData("casc-extraction")] // the extraction root: <selection>/data
    public void CascLayoutsAreRecognisedExplicitly(string layout)
    {
        var (data, selected) = layout switch
        {
            "casc-flat" => (MakeDataRoot("casc-flat", strings: false, hd: true), Path.Combine(_root, "casc-flat")),
            "casc-nested" => (MakeDataRoot(Path.Combine("casc-nested", "data", "data"), hd: true), Path.Combine(_root, "casc-nested", "data")),
            _ => (MakeDataRoot(Path.Combine("casc-extraction", "data"), excel: false, hd: true), Path.Combine(_root, "casc-extraction")),
        };
        var result = SourceRoots.ValidateCascRoot(selected);
        Assert.True(result.IsValid, result.Message);
        Assert.Equal(data, result.Root);
    }

    [Fact]
    public void TheNestedCascSelectionFromTheExtractionRootIsResolvedTwoLevelsDown()
    {
        var data = MakeDataRoot(Path.Combine("z_Casc", "data", "data"), hd: true);
        Directory.CreateDirectory(Path.Combine(_root, "z_Casc", "data", "locales"));
        Assert.Equal(data, SourceRoots.ValidateCascRoot(Path.Combine(_root, "z_Casc")).Root);
        Assert.Equal(data, SourceRoots.ValidateCascRoot(Path.Combine(_root, "z_Casc", "data")).Root);
    }

    [Fact]
    public void InvalidRootsReportSelectionExpectationAndWhatWasFound()
    {
        var wrong = Path.Combine(_root, "wrong");
        Directory.CreateDirectory(Path.Combine(wrong, "textures"));
        File.WriteAllText(Path.Combine(wrong, "readme.txt"), "");

        var mod = SourceRoots.ValidateModRoot(wrong);
        Assert.False(mod.IsValid);
        Assert.Contains(wrong, mod.Message);
        Assert.Contains("'global/excel'", mod.Message);
        Assert.Contains("<selected>/data", mod.Message);
        Assert.Contains("textures", mod.Message);
        Assert.Contains("readme.txt", mod.Message);

        var missing = SourceRoots.ValidateCascRoot(Path.Combine(_root, "nope"));
        Assert.False(missing.IsValid);
        Assert.Contains("does not exist", missing.Message);

        Assert.False(SourceRoots.ValidateModRoot("").IsValid);
        Assert.Throws<SourceLayoutException>(() => SourceRoots.Create(wrong, null));

        // A valid mod with an unsupported CASC selection is refused, not silently ignored.
        var data = MakeDataRoot("mod2");
        var ex = Assert.Throws<SourceLayoutException>(() => SourceRoots.Create(data, wrong));
        Assert.Contains("CASC Data Directory", ex.Message);
    }

    [Fact]
    public void ModFilesWinAndCascFillsOnlyWhatTheModDoesNotShip()
    {
        var mod = MakeDataRoot("mod");
        var casc = MakeDataRoot("casc", hd: true);
        Write(Path.Combine(mod, "global", "excel", "weapons.txt"), "mod");
        Write(Path.Combine(casc, "global", "excel", "weapons.txt"), "vanilla");
        Write(Path.Combine(casc, "global", "excel", "rareprefix.txt"), "vanilla");
        Write(Path.Combine(mod, "hd", "global", "ui", "items", "misc", "ring", "ring.sprite"), "mod");
        Write(Path.Combine(casc, "hd", "global", "ui", "items", "misc", "amulet", "amulet.sprite"), "vanilla");

        var sources = SourceRoots.Create(mod, casc);

        var weapons = sources.ExcelSource("weapons.txt");
        Assert.Equal(SourceOrigin.Mod, weapons.Origin);
        Assert.Equal("mod", File.ReadAllText(weapons.Path!));

        var rare = sources.ExcelSource("rareprefix.txt");
        Assert.Equal(SourceOrigin.Casc, rare.Origin);
        Assert.Equal(2, rare.Searched.Count);

        Assert.Equal(SourceOrigin.Mod, sources.Resolve("hd/global/ui/items/misc/ring/ring.sprite").Origin);
        Assert.Equal(SourceOrigin.Casc, sources.Resolve("hd/global/ui/items/misc/amulet/amulet.sprite").Origin);

        var none = sources.Resolve("hd/global/ui/items/misc/scepter/scepter.sprite");
        Assert.False(none.Exists);
        Assert.Equal(2, none.Searched.Count);
        Assert.StartsWith(mod, none.Searched[0]);
        Assert.StartsWith(casc, none.Searched[1]);

        // Without a CASC root there is no fallback, and nothing unrelated is substituted.
        var modOnly = SourceRoots.Create(mod, null);
        Assert.False(modOnly.ExcelSource("rareprefix.txt").Exists);
        Assert.Single(modOnly.ExcelSource("rareprefix.txt").Searched);
    }

    [Fact]
    public void LookupsAreCaseInsensitiveAndAcceptEitherSeparator()
    {
        var mod = MakeDataRoot("mod");
        Write(Path.Combine(mod, "global", "excel", "runes.txt"), "x");
        Write(Path.Combine(mod, "hd", "items", "items.json"), "[]");
        var sources = SourceRoots.Create(mod, null);

        Assert.True(sources.ExcelSource("Runes.txt").Exists); // referenced in mixed case, lower case on disk
        Assert.True(sources.Resolve("HD/Items/ITEMS.json").Exists);
        Assert.True(sources.Resolve(@"hd\items\items.json").Exists); // Windows-style separators
        Assert.True(sources.Resolve("hd/items/items.json").Exists);  // Linux-style separators
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("hd/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"c:\Windows\win.ini")]
    [InlineData(@"C:Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData(@"\Windows\win.ini")]
    [InlineData("")]
    public void LookupsCannotLeaveTheirRoot(string relative)
    {
        var sources = SourceRoots.Create(MakeDataRoot("mod"), null);
        Assert.Throws<ArgumentException>(() => sources.Resolve(relative));
    }

    [Fact]
    public void ReferenceTablesFallBackToTheBundledCopyLast()
    {
        var mod = MakeDataRoot("mod");
        var casc = MakeDataRoot("casc");
        var bundled = Path.Combine(_root, "bundled");
        Write(Path.Combine(bundled, "misscalc.txt"), "bundled");
        Write(Path.Combine(bundled, "skillcalc.txt"), "bundled");
        Write(Path.Combine(casc, "global", "excel", "skillcalc.txt"), "vanilla");

        var sources = SourceRoots.Create(mod, casc, bundled);
        Assert.Equal(SourceOrigin.Casc, sources.ResolveReferenceTable("skillcalc.txt").Origin);
        Assert.Equal(SourceOrigin.Bundled, sources.ResolveReferenceTable("misscalc.txt").Origin);
        Assert.Contains(sources.Lookups, l => l.RelativePath == "global/excel/misscalc.txt" && l.Origin == SourceOrigin.Bundled);
    }

    [Fact]
    public void ThePipelineResolvesTheSameRootsWhateverTheWorkingDirectory()
    {
        var mod = MakeDataRoot(Path.Combine("repo", "data"));
        var casc = MakeDataRoot(Path.Combine("casc", "data", "data"), hd: true);
        var output = Path.Combine(_root, "out");
        var previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root);
            var relative = new D2RMultiExportPipeline(Path.Combine("repo"), Path.Combine("casc", "data"), "out");
            Directory.SetCurrentDirectory(Path.GetTempPath());
            var absolute = new D2RMultiExportPipeline(mod, Path.Combine(_root, "casc"), output);
            Assert.Equal(absolute.Sources.ModDataRoot, relative.Sources.ModDataRoot);
            Assert.Equal(absolute.Sources.CascDataRoot, relative.Sources.CascDataRoot);
            Assert.Equal(casc, absolute.Sources.CascDataRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void RecognizedDirectoriesRetainTheirActualCaseForExcelAndTranslations()
    {
        var mod = Path.Combine(_root, "MixedCaseMod");
        var excel = Directory.CreateDirectory(Path.Combine(mod, "Global", "Excel")).FullName;
        var strings = Directory.CreateDirectory(Path.Combine(mod, "Local", "Lng", "Strings")).FullName;
        Write(Path.Combine(excel, "Skills.txt"), "test");
        Write(Path.Combine(strings, "Test.json"), "[]");
        var sources = SourceRoots.Create(mod, null);
        Assert.Equal(excel, sources.ModExcelDirectory);
        Assert.Equal(strings, sources.ModStringsDirectory);
        Assert.True(Directory.Exists(sources.ModExcelDirectory));
        Assert.True(Directory.Exists(sources.ModStringsDirectory));
        Assert.Equal(excel, Path.GetDirectoryName(sources.ExcelSource("skills.txt").Path));
        Assert.Single(Directory.GetFiles(sources.ModStringsDirectory, "*.json"));
    }

}
