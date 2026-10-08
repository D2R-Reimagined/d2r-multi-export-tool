// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>Interrupted writes, abandoned temp files, the link race, and publish staging.</summary>
public sealed class WriteSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-write-safety-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Out => Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;

    [Fact]
    public async Task ASerializerThatDiesMidStreamLeavesThePreviousFileAndNoTemp()
    {
        var outputs = new ExportOutputs(Out) { Producer = "test" };
        await outputs.WriteTextAsync("keyed/a.json", "\"old\"");

        await Assert.ThrowsAsync<IOException>(() => outputs.WriteAsync("keyed/a.json", async stream =>
        {
            await stream.WriteAsync("\"partial"u8.ToArray());
            throw new IOException("simulated crash during serialization");
        }));

        Assert.Equal("\"old\"", await File.ReadAllTextAsync(Path.Combine(Out, "keyed", "a.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(Out, "keyed"), "*.tmp"));
    }

    [Fact]
    public async Task AKilledProcessLeavesTheOldFileAndTheNextWriteRemovesOnlyItsOwnTemps()
    {
        var keyed = Directory.CreateDirectory(Path.Combine(Out, "keyed")).FullName;
        await File.WriteAllTextAsync(Path.Combine(keyed, "a.json"), "\"old\"");
        // What a process killed before the rename leaves behind: a full-shaped temp file.
        var abandoned = Path.Combine(keyed, AtomicFile.TempName("a.json"));
        await File.WriteAllTextAsync(abandoned, "\"half");
        // Files that are NOT this tool's temp for a.json and must survive.
        var foreign = new[]
        {
            Path.Combine(keyed, ".a.json.mine.tmp"),                       // not 32 hex
            Path.Combine(keyed, AtomicFile.TempName("b.json")),            // another output's temp
            Path.Combine(keyed, "a.json.tmp"),                             // no leading dot
            Path.Combine(keyed, ".a.json." + new string('A', 32) + ".tmp") // upper-case hex: not ours
        };
        foreach (var f in foreign) await File.WriteAllTextAsync(f, "x");

        // The interrupted state still reads the previous complete document.
        Assert.Equal("\"old\"", await File.ReadAllTextAsync(Path.Combine(keyed, "a.json")));

        var outputs = new ExportOutputs(Out) { Producer = "test" };
        await outputs.WriteTextAsync("keyed/a.json", "\"new\"");

        Assert.Equal("\"new\"", await File.ReadAllTextAsync(Path.Combine(keyed, "a.json")));
        Assert.False(File.Exists(abandoned));
        Assert.All(foreign, f => Assert.True(File.Exists(f), f));
        Assert.Equal(1, outputs.AbandonedTempsRemoved);
    }

    [Fact]
    public async Task ATempStillHeldOpenByAnotherWriterIsNotDeleted()
    {
        var keyed = Directory.CreateDirectory(Path.Combine(Out, "keyed")).FullName;
        var busy = Path.Combine(keyed, AtomicFile.TempName("a.json"));
        await using (new FileStream(busy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            Assert.Equal(0, AtomicFile.RemoveAbandonedTemps(Path.Combine(keyed, "a.json")));
            Assert.True(File.Exists(busy));
        }
    }

    [Fact]
    public async Task ALinkPlantedAfterValidationIsRefusedBeforeAnyByteIsWritten()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return; // handle-based check is Windows/Linux only
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var outputs = new ExportOutputs(Out) { Producer = "test" };
        var target = Path.Combine(Out, "sprites");
        var linked = false;
        outputs.AfterValidation = _ =>
        {
            // The race: the path validated as a plain (absent) directory, then becomes a link.
            linked = TryLink(target, outside);
        };

        var ex = await Record.ExceptionAsync(() => outputs.WriteTextAsync("sprites/x.webp", "payload"));
        Assert.True(linked, "could not create a symbolic link or junction for the race");
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("outside the export directory", ex.Message);
        Assert.Empty(Directory.GetFiles(outside));
        Assert.False(outputs.WasWritten("sprites/x.webp"));
        Directory.Delete(target); // removes the link only, never its target
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!OperatingSystem.IsWindows()) return false;
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
    }

    private async Task<ExportOutputs> RunAsync(bool skipStep = false, bool fail = false)
    {
        var outputs = new ExportOutputs(Out) { Producer = "keyed-core" };
        await outputs.WriteTextAsync("keyed/uniques.json", "[1]");
        outputs.Producer = "skill-icons";
        await outputs.WriteTextAsync("sprites/skills/a.webp", "img");
        outputs.Producer = "";
        var steps = new List<ExportStepResult>
        {
            new("keyed-core", ExportStepStatus.Written, ["keyed/uniques.json"], [], [], null),
            new("rare-names.json", skipStep ? ExportStepStatus.Skipped : ExportStepStatus.Written, [], [], [], skipStep ? "rareprefix.txt missing" : null)
        };
        await outputs.WriteManifestAsync(!fail, steps, prettyPrint: true);
        return outputs;
    }

    [Fact]
    public async Task StagingCopiesOnlyTheRunsVerifiedFiles()
    {
        await RunAsync();
        // A stale file from an earlier run / another tool, sitting in the output directory.
        Directory.CreateDirectory(Path.Combine(Out, "keyed"));
        await File.WriteAllTextAsync(Path.Combine(Out, "keyed", "rare-names.json"), "[\"stale\"]");
        await File.WriteAllTextAsync(Path.Combine(Out, "notes.txt"), "mine");
        var before = Directory.GetFiles(Out, "*", SearchOption.AllDirectories).Order().ToList();

        var staging = Path.Combine(_root, "staging");
        var result = await PublishStager.StageAsync(Out, staging, allowIncomplete: false);

        Assert.True(result.Staged, string.Join("; ", result.Problems));
        var staged = Directory.GetFiles(staging, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(staging, f).Replace('\\', '/')).Order().ToList();
        Assert.Equal(["keyed/uniques.json", "manifest.json", "sprites/skills/a.webp"], staged);
        // The user's output directory is untouched.
        Assert.Equal(before, Directory.GetFiles(Out, "*", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact]
    public async Task StagingRefusesFailedIncompleteTamperedOrUnsafeRuns()
    {
        await RunAsync(fail: true);
        Assert.Contains("Succeeded: false", Assert.Single((await PublishStager.StageAsync(Out, Path.Combine(_root, "s1"), true)).Problems));

        await RunAsync(skipStep: true);
        var incomplete = await PublishStager.StageAsync(Out, Path.Combine(_root, "s2"), allowIncomplete: false);
        Assert.False(incomplete.Staged);
        Assert.Contains("rare-names.json (Skipped)", incomplete.Problems[0]);
        Assert.False(Directory.Exists(Path.Combine(_root, "s2")));
        var allowed = await PublishStager.StageAsync(Out, Path.Combine(_root, "s3"), allowIncomplete: true);
        Assert.True(allowed.Staged);

        await RunAsync();
        await File.WriteAllTextAsync(Path.Combine(Out, "keyed", "uniques.json"), "[2]");
        var tampered = await PublishStager.StageAsync(Out, Path.Combine(_root, "s4"), false);
        Assert.False(tampered.Staged);
        Assert.Contains("changed since the run", tampered.Problems[0]);
        Assert.False(Directory.Exists(Path.Combine(_root, "s4")));

        await RunAsync();
        Assert.Contains("must not be the output directory",
            (await PublishStager.StageAsync(Out, Path.Combine(Out, "publish"), false)).Problems[0]);
        var nonEmpty = Directory.CreateDirectory(Path.Combine(_root, "s5")).FullName;
        await File.WriteAllTextAsync(Path.Combine(nonEmpty, "keep.txt"), "x");
        Assert.Contains("not empty", (await PublishStager.StageAsync(Out, nonEmpty, false)).Problems[0]);
        Assert.True(File.Exists(Path.Combine(nonEmpty, "keep.txt")));

        File.Delete(Path.Combine(Out, ExportOutputs.ManifestFileName));
        Assert.Contains("does not exist", (await PublishStager.StageAsync(Out, Path.Combine(_root, "s6"), false)).Problems[0]);
    }

    [Fact]
    public async Task ManifestRecordsAbandonedTempCleanup()
    {
        var keyed = Directory.CreateDirectory(Path.Combine(Out, "keyed")).FullName;
        await File.WriteAllTextAsync(Path.Combine(keyed, AtomicFile.TempName("uniques.json")), "half");
        await RunAsync();
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Out, "manifest.json")));
        Assert.Equal(1, doc.RootElement.GetProperty("AbandonedTempFilesRemoved").GetInt32());
    }

    [Theory]
    [InlineData("keyed/uniques.json", "[2]")]
    [InlineData("sprites/skills/a.webp", "new image")]
    public async Task ASourceReplacedAfterVerificationCannotProduceACompletedStage(string changedPath, string content)
    {
        await RunAsync();
        var staging = Path.Combine(_root, "changed-source");
        var manifestBefore = await File.ReadAllBytesAsync(Path.Combine(Out, "manifest.json"));
        var result = await PublishStager.StageAsync(Out, staging, false, async () =>
        {
            // Same window as a concurrent export's atomic replacement, after preflight.
            var replacement = Path.Combine(_root, "replacement.json");
            await File.WriteAllTextAsync(replacement, content);
            File.Move(replacement, Path.Combine(Out, changedPath), overwrite: true);
        });
        Assert.False(result.Staged);
        Assert.Contains("changed while staging", Assert.Single(result.Problems));
        Assert.False(File.Exists(Path.Combine(staging, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(staging, changedPath)));
        if (changedPath.StartsWith("sprites/", StringComparison.Ordinal))
        {
            Assert.Equal("[1]", await File.ReadAllTextAsync(Path.Combine(staging, "keyed", "uniques.json")));
            Assert.Equal("keyed/uniques.json", Assert.Single(result.Files));
        }
        else
        {
            Assert.Empty(result.Files);
        }
        Assert.Equal(manifestBefore, await File.ReadAllBytesAsync(Path.Combine(Out, "manifest.json")));
        Assert.Equal(content, await File.ReadAllTextAsync(Path.Combine(Out, changedPath)));
    }

    [Fact]
    public async Task ManifestReplacementCannotChangeTheCapturedStagingGeneration()
    {
        await RunAsync();
        var before = await File.ReadAllBytesAsync(Path.Combine(Out, "manifest.json"));
        var staging = Path.Combine(_root, "changed-manifest");
        var result = await PublishStager.StageAsync(Out, staging, false,
            () => File.WriteAllTextAsync(Path.Combine(Out, "manifest.json"), "{\"Succeeded\":false}"));
        Assert.True(result.Staged, string.Join("; ", result.Problems));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(staging, "manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagingRefusesLinksAndUncreatedChildrenPhysicallyInsideOutput(bool uncreatedChild)
    {
        if (!RealPath.IsSupported) return;
        await RunAsync();
        var nested = Directory.CreateDirectory(Path.Combine(Out, "nested-stage")).FullName;
        var alias = Path.Combine(_root, "stage-alias");
        Assert.True(TryLink(alias, nested));
        try
        {
            var result = await PublishStager.StageAsync(Out,
                uncreatedChild ? Path.Combine(alias, "not-created", "stage") : alias, false);
            Assert.False(result.Staged);
            Assert.Contains("must not be the output directory", Assert.Single(result.Problems));
            Assert.Empty(Directory.EnumerateFileSystemEntries(nested));
        }
        finally
        {
            Directory.Delete(alias); // the link only
        }
    }


    [Fact]
    public async Task AnOutputRootAliasCannotHidePhysicalNesting()
    {
        if (!RealPath.IsSupported) return;
        await RunAsync();
        var alias = Path.Combine(_root, "output-alias");
        Assert.True(TryLink(alias, Out));
        try
        {
            var result = await PublishStager.StageAsync(alias, Path.Combine(Out, "stage"), false);
            Assert.False(result.Staged);
            Assert.Contains("must not be the output directory", Assert.Single(result.Problems));
            Assert.False(Directory.Exists(Path.Combine(Out, "stage")));
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

}
