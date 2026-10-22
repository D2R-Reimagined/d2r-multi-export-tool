// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>
/// The two export modes: the default writes into the output directory and leaves unrelated
/// files alone; the destructive mode replaces the directory's contents only after a run
/// without errors, and keeps or restores them otherwise.
/// </summary>
public sealed class DestructiveExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-destructive-tests-").FullName;
    private readonly string _out;

    public DestructiveExportTests()
    {
        _out = Path.Combine(_root, "out");
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Every file below <paramref name="directory"/> with its content, by <c>/</c>-relative path.</summary>
    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(directory, path).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);

    private void SeedPreviousContents()
    {
        Directory.CreateDirectory(Path.Combine(_out, "keyed"));
        Directory.CreateDirectory(Path.Combine(_out, "custom", "nested"));
        File.WriteAllText(Path.Combine(_out, "keyed", "uniques.json"), "old uniques");
        File.WriteAllText(Path.Combine(_out, "keyed", "obsolete.json"), "obsolete");
        File.WriteAllText(Path.Combine(_out, "custom", "nested", "unrelated.txt"), "unrelated");
        File.WriteAllText(Path.Combine(_out, "README.txt"), "hand-written");
    }

    /// <summary>A stand-in for one export run: writes through <see cref="ExportOutputs"/> and seals it with a manifest.</summary>
    private static async Task WriteExportAsync(string root, string uniques, params string[] extra)
    {
        var outputs = new ExportOutputs(root) { Producer = "keyed-core" };
        await outputs.WriteTextAsync("keyed/uniques.json", uniques);
        foreach (var path in extra) await outputs.WriteTextAsync(path, path);
        await outputs.WriteManifestAsync(succeeded: true,
            [new ExportStepResult("keyed-core", ExportStepStatus.Written, [.. outputs.Files.Select(static f => f.Path)], [], [], null)],
            prettyPrint: false);
    }

    private static List<string> ManifestPaths(string root)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ExportOutputs.ManifestFileName)));
        return [.. doc.RootElement.GetProperty("Files").EnumerateArray().Select(static f => f.GetProperty("Path").GetString()!)];
    }

    private void AssertNoWorkingDirectories() =>
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_out), entry => Path.GetFileName(entry).StartsWith(".d2r-export-", StringComparison.Ordinal));

    [Fact]
    public async Task DefaultExportOverwritesMatchingFilesAndLeavesEverythingElseOnRepeatedRuns()
    {
        SeedPreviousContents();

        await WriteExportAsync(_out, "new uniques", "keyed/runewords.json");
        await WriteExportAsync(_out, "newer uniques", "keyed/sets.json");

        var files = Snapshot(_out);
        Assert.Equal("newer uniques", files["keyed/uniques.json"]);
        Assert.Equal("obsolete", files["keyed/obsolete.json"]);
        Assert.Equal("unrelated", files["custom/nested/unrelated.txt"]);
        Assert.Equal("hand-written", files["README.txt"]);
        Assert.Equal("keyed/runewords.json", files["keyed/runewords.json"]);
        // The manifest describes the current run only, not everything in the directory.
        Assert.Equal(["keyed/sets.json", "keyed/uniques.json"], ManifestPaths(_out).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DestructiveExportLeavesOnlyTheNewExportAndRepeatsCleanly()
    {
        SeedPreviousContents();

        var first = await DestructiveReplacement.RunAsync(_out, async working =>
        {
            await WriteExportAsync(working, "new uniques", "keyed/runewords.json");
            return true;
        });

        Assert.True(first.Replaced);
        Assert.Equal(3, first.RemovedEntries);
        Assert.Null(first.Warning);
        Assert.Equal(["keyed/runewords.json", "keyed/uniques.json", "manifest.json"], Snapshot(_out).Keys.Order(StringComparer.Ordinal));
        Assert.Equal("new uniques", File.ReadAllText(Path.Combine(_out, "keyed", "uniques.json")));
        Assert.Equal(["keyed/runewords.json", "keyed/uniques.json"], ManifestPaths(_out).Order(StringComparer.Ordinal));
        Assert.Equal(["keyed", "manifest.json"], Directory.EnumerateFileSystemEntries(_out).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        var second = await DestructiveReplacement.RunAsync(_out, async working =>
        {
            await WriteExportAsync(working, "second uniques", "keyed/sets.json");
            return true;
        });

        Assert.True(second.Replaced);
        Assert.Equal(["keyed/sets.json", "keyed/uniques.json", "manifest.json"], Snapshot(_out).Keys.Order(StringComparer.Ordinal));
        Assert.Equal("second uniques", File.ReadAllText(Path.Combine(_out, "keyed", "uniques.json")));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task UnsuccessfulGenerationLeavesTheDestinationUnchanged()
    {
        SeedPreviousContents();
        var before = Snapshot(_out);

        var result = await DestructiveReplacement.RunAsync(_out, async working =>
        {
            await WriteExportAsync(working, "partial uniques");
            return false;
        });

        Assert.False(result.Replaced);
        Assert.Equal(before, Snapshot(_out));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task ThrowingGenerationLeavesTheDestinationUnchanged()
    {
        SeedPreviousContents();
        var before = Snapshot(_out);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DestructiveReplacement.RunAsync(_out, async working =>
        {
            await WriteExportAsync(working, "partial uniques");
            throw new InvalidOperationException("exporter blew up");
        }));

        Assert.Equal(before, Snapshot(_out));
        AssertNoWorkingDirectories();
    }

    [Theory]
    [InlineData(2)]  // while moving the previous contents aside
    [InlineData(5)]  // while moving the new export in
    public async Task FailedReplacementRestoresThePreviousContents(int failingMove)
    {
        SeedPreviousContents();
        var before = Snapshot(_out);
        var moves = 0;

        var error = await Assert.ThrowsAsync<IOException>(() => DestructiveReplacement.RunAsync(_out, async working =>
            {
                await WriteExportAsync(working, "new uniques", "extras/report.txt");
                return true;
            },
            (from, to, isDirectory) =>
            {
                // Fails every retry of this one move; the rollback moves succeed.
                if (++moves >= failingMove && moves < failingMove + 4) throw new IOException("locked by another process");
                if (isDirectory) Directory.Move(from, to);
                else File.Move(from, to);
            }));

        Assert.Contains("previous contents were restored", error.Message);
        Assert.Equal(before, Snapshot(_out));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task LinksInsideTheDestinationAreRemovedWithoutTouchingTheirTargets()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "outside");
        var link = Path.Combine(_out, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Symlinks need Developer Mode or elevation on Windows; a junction needs neither.
            if (!OperatingSystem.IsWindows()) throw;
            var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            await junction.WaitForExitAsync();
            Assert.Equal(0, junction.ExitCode);
        }

        var result = await DestructiveReplacement.RunAsync(_out, async working =>
        {
            await WriteExportAsync(working, "new uniques");
            return true;
        });

        Assert.True(result.Replaced);
        Assert.False(Path.Exists(link));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public void DangerousDestinationsAreRefused()
    {
        var source = Path.Combine(_root, "mod");
        Directory.CreateDirectory(source);
        (string, string?)[] sources = [("the Mod Data Directory", source)];

        Assert.Null(DestructiveReplacement.FindDanger(_out, sources));
        Assert.Contains("root", DestructiveReplacement.FindDanger(Path.GetPathRoot(_out)!, sources));
        Assert.Contains("user or system folder", DestructiveReplacement.FindDanger(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), sources));
        Assert.Contains("program folder", DestructiveReplacement.FindDanger(AppContext.BaseDirectory, sources));
        Assert.Contains("contains the Mod Data Directory", DestructiveReplacement.FindDanger(_root, sources));
        Assert.Contains("is the Mod Data Directory", DestructiveReplacement.FindDanger(source, sources));
        Assert.Contains("inside the Mod Data Directory", DestructiveReplacement.FindDanger(Path.Combine(source, "global"), sources));

        var repository = Path.Combine(_root, "website");
        Directory.CreateDirectory(Path.Combine(repository, ".git"));
        Directory.CreateDirectory(Path.Combine(repository, "static", "data"));
        Assert.Contains("Git repository root", DestructiveReplacement.FindDanger(repository, sources));
        // The usual website destination inside the repository is fine.
        Assert.Null(DestructiveReplacement.FindDanger(Path.Combine(repository, "static", "data"), sources));
    }

    private (string Mod, D2RMultiExportPipeline Pipeline) CreatePipeline(string output, bool continueOnException)
    {
        // An empty mod: the run records errors (or throws) before it could vouch for anything.
        var mod = Path.Combine(_root, "mod", "data");
        Directory.CreateDirectory(Path.Combine(mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(mod, "local", "lng", "strings"));
        return (mod, new D2RMultiExportPipeline(mod, null, output)
        {
            DestructiveExport = true,
            ContinueOnException = continueOnException
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedDestructivePipelineRunKeepsTheDestination(bool continueOnException)
    {
        SeedPreviousContents();
        var before = Snapshot(_out);
        var (_, pipeline) = CreatePipeline(_out, continueOnException);

        try
        {
            await pipeline.RunAsync();
        }
        catch (Exception) when (!continueOnException)
        {
            // Without ContinueOnException the first failed phase aborts the run.
        }

        Assert.True(pipeline.Result.HasErrors);
        Assert.Equal(before, Snapshot(_out));
        AssertNoWorkingDirectories();
    }

    [Fact]
    public async Task DestructivePipelineRunIntoARepositoryRootIsRefusedBeforeAnythingChanges()
    {
        File.WriteAllText(Path.Combine(_out, "package.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_out, ".git"));
        var before = Snapshot(_out);
        var (_, pipeline) = CreatePipeline(_out, continueOnException: true);

        Assert.NotNull(pipeline.FindDestructiveExportDanger());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(pipeline.RunAsync);

        Assert.Contains("Git repository root", error.Message);
        Assert.Equal(before, Snapshot(_out));
        AssertNoWorkingDirectories();
    }
}
