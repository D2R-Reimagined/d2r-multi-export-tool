// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>Interrupted writes, abandoned temp files, the link race, and manifest temp cleanup.</summary>
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

    private async Task<ExportOutputs> RunAsync()
    {
        var outputs = new ExportOutputs(Out) { Producer = "keyed-core" };
        await outputs.WriteTextAsync("keyed/uniques.json", "[1]");
        outputs.Producer = "skill-icons";
        await outputs.WriteTextAsync("sprites/skills/a.webp", "img");
        outputs.Producer = "";
        var steps = new List<ExportStepResult>
        {
            new("keyed-core", ExportStepStatus.Written, ["keyed/uniques.json"], [], [], null),
            new("rare-names.json", ExportStepStatus.Written, [], [], [], null)
        };
        await outputs.WriteManifestAsync(true, steps, prettyPrint: true);
        return outputs;
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
}
