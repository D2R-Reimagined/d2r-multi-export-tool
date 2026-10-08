// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using D2RMultiExport.Lib;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>Regression coverage for atomic writes, stale-output removal and the run manifest.</summary>
public sealed class OutputIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "d2r-export-tests", Guid.NewGuid().ToString("N"));

    public OutputIntegrityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Exploding
    {
        public string First => "ok";
        public string Second => throw new InvalidOperationException("serializer blew up");
    }

    [Fact]
    public async Task FailedSerializationKeepsPreviousFileIntact()
    {
        var outputs = new ExportOutputs(_root);
        await outputs.WriteJsonAsync("keyed/a.json", new { Value = 1 }, null);
        var before = await File.ReadAllTextAsync(outputs.GetFullPath("keyed/a.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outputs.WriteJsonAsync("keyed/a.json", new Exploding(), null));

        Assert.Equal(before, await File.ReadAllTextAsync(outputs.GetFullPath("keyed/a.json")));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "keyed")));
    }

    [Fact]
    public async Task FailedFirstWriteLeavesNoFileAndIsNotRecorded()
    {
        var outputs = new ExportOutputs(_root);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outputs.WriteJsonAsync("keyed/b.json", new Exploding(), null));

        Assert.False(File.Exists(outputs.GetFullPath("keyed/b.json")));
        Assert.False(outputs.WasWritten("keyed/b.json"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "keyed")));
    }

    [Fact]
    public void PathsEscapingTheExportRootAreRefused()
    {
        var outputs = new ExportOutputs(_root);
        Assert.Throws<InvalidOperationException>(() => outputs.GetFullPath("../outside.json"));
        Assert.Throws<InvalidOperationException>(() => outputs.GetFullPath(Path.Combine("..", Path.GetFileName(_root) + "x", "f.json")));
    }

    [Fact]
    public void RootedAndEmptyPathsAreRefused()
    {
        var outputs = new ExportOutputs(_root);
        Assert.Throws<InvalidOperationException>(() => outputs.GetFullPath(Path.Combine(_root, "keyed", "a.json")));
        Assert.Throws<InvalidOperationException>(() => outputs.GetFullPath(""));
    }

    [Fact]
    public async Task WritesThroughASymbolicLinkBelowTheRootAreRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "d2r-export-tests", Guid.NewGuid().ToString("N") + "-outside");
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(_root, "sprites");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Creating symlinks needs Developer Mode or elevation on Windows; fall back
                // to a junction, which needs neither and is the redirection that matters there.
                if (!OperatingSystem.IsWindows()) throw;
                var junction = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    $"/c mklink /J \"{link}\" \"{outside}\"") { CreateNoWindow = true, UseShellExecute = false })!;
                await junction.WaitForExitAsync();
                Assert.Equal(0, junction.ExitCode);
            }

            var outputs = new ExportOutputs(_root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => outputs.WriteTextAsync("sprites/x.webp", "x"));
            Assert.Empty(Directory.GetFiles(outside));
            Assert.Empty(outputs.InvalidateAttempted(""));
        }
        finally
        {
            var link = Path.Combine(_root, "sprites");
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidationRemovesOnlyPathsTheProducerAttempted()
    {
        var manual = Path.Combine(_root, "keyed", "manual.json");
        var otherStep = Path.Combine(_root, "keyed", "other.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manual)!);
        await File.WriteAllTextAsync(manual, "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "keyed", "failed.json"), "old");

        var outputs = new ExportOutputs(_root) { Producer = "other" };
        await outputs.WriteTextAsync("keyed/other.json", "new");
        outputs.Producer = "broken";
        await outputs.WriteTextAsync("keyed/fresh.json", "new");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            outputs.WriteJsonAsync("keyed/failed.json", new Exploding(), null));

        var removed = outputs.InvalidateAttempted("broken");

        Assert.Equal(["keyed/failed.json", "keyed/fresh.json"], removed.OrderBy(static p => p, StringComparer.Ordinal));
        Assert.True(File.Exists(manual));
        Assert.True(File.Exists(otherStep));
        Assert.False(outputs.WasWritten("keyed/fresh.json"));
        Assert.True(outputs.WasWritten("keyed/other.json"));
    }

    [Fact]
    public async Task ManifestListsOnlyThisRunsFilesWithHashes()
    {
        var leftover = Path.Combine(_root, "keyed", "leftover.json");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "{}");

        var outputs = new ExportOutputs(_root) { Producer = "test" };
        await outputs.WriteTextAsync("extras/report.txt", "hello");
        await outputs.WriteManifestAsync(succeeded: false,
            [new ExportStepResult("test", ExportStepStatus.Written, ["extras/report.txt"], [], [], null)], prettyPrint: false);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, ExportOutputs.ManifestFileName)));
        Assert.Equal(ExportOutputs.ManifestSchemaVersion, manifest.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.False(manifest.RootElement.GetProperty("Succeeded").GetBoolean());
        Assert.False(manifest.RootElement.GetProperty("Complete").GetBoolean());
        Assert.True(File.Exists(leftover));
        var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToList();
        var file = Assert.Single(files);
        Assert.Equal("extras/report.txt", file.GetProperty("Path").GetString());
        Assert.Equal(5, file.GetProperty("Bytes").GetInt64());
        // SHA-256("hello")
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", file.GetProperty("Sha256").GetString());
    }

    private (D2RMultiExportPipeline Pipeline, Func<ExportOutputs, string, Func<Task>, Task> RunReport,
        IReadOnlyList<ExportStepResult> Steps) CreateReportRunner()
    {
        var mod = Path.Combine(_root, "mod");
        Directory.CreateDirectory(Path.Combine(mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(mod, "local", "lng", "strings"));
        var pipeline = new D2RMultiExportPipeline(mod, null, _root);
        // Exercise the isolated report phase without loading game data or widening the public API.
        var method = typeof(D2RMultiExportPipeline).GetMethod("RunReportStepAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var steps = (IReadOnlyList<ExportStepResult>)typeof(D2RMultiExportPipeline)
            .GetField("_exportSteps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pipeline)!;
        return (pipeline, method.CreateDelegate<Func<ExportOutputs, string, Func<Task>, Task>>(pipeline), steps);
    }

    [Fact]
    public async Task DiagnosticReportsUseTheirManifestStepAsProducerAndIsolateFailures()
    {
        var (pipeline, runReport, steps) = CreateReportRunner();
        var outputs = new ExportOutputs(_root) { Producer = "extras" };
        const string first = "extras/missing-translations.txt";
        const string failed = "extras/asset-report.txt";
        const string last = "extras/import-report.txt";
        await runReport(outputs, first, () => outputs.WriteTextAsync(first, "no missing keys"));
        Assert.Equal("extras", outputs.Producer);
        Assert.Equal([first], outputs.AttemptedBy(first));

        await File.WriteAllTextAsync(outputs.GetFullPath(failed), "old report");
        await runReport(outputs, failed, () => outputs.WriteAsync(failed,
            _ => throw new IOException("report write failed")));
        Assert.Equal("extras", outputs.Producer);
        Assert.False(File.Exists(outputs.GetFullPath(failed)));
        Assert.True(outputs.WasWritten(first));
        await runReport(outputs, last, () => outputs.WriteTextAsync(last, pipeline.Result.GenerateReport()));
        Assert.Equal("extras", outputs.Producer);
        Assert.True(pipeline.Result.HasErrors);
        Assert.Equal([first, last], outputs.Files.Select(static f => f.Producer));
        Assert.All(outputs.Files, file => Assert.Equal(file.Path, file.Producer));

        await outputs.WriteManifestAsync(succeeded: !pipeline.Result.HasErrors, steps, prettyPrint: false);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, ExportOutputs.ManifestFileName)));
        var root = manifest.RootElement;
        Assert.Equal(2, root.GetProperty("SchemaVersion").GetInt32());
        Assert.False(root.GetProperty("Succeeded").GetBoolean());
        Assert.False(root.GetProperty("Complete").GetBoolean());
        var manifestSteps = root.GetProperty("Steps").EnumerateArray().ToList();
        Assert.Equal(3, manifestSteps.Count);
        foreach (var file in root.GetProperty("Files").EnumerateArray())
        {
            var step = Assert.Single(manifestSteps, s => s.GetProperty("Producer").GetString() == file.GetProperty("Producer").GetString());
            Assert.Equal("Written", step.GetProperty("Status").GetString());
            Assert.Equal(file.GetProperty("Path").GetString(), step.GetProperty("Outputs")[0].GetString());
        }
        var failedStep = Assert.Single(manifestSteps, s => s.GetProperty("Producer").GetString() == failed);
        Assert.Equal("Failed", failedStep.GetProperty("Status").GetString());
        Assert.Equal(failed, failedStep.GetProperty("Removed")[0].GetString());
        Assert.DoesNotContain(root.GetProperty("Files").EnumerateArray(), f => f.GetProperty("Path").GetString() == failed);
    }

    [Fact]
    public async Task ReportFailureBeforeWriteLeavesExistingFileUnchanged()
    {
        var (_, runReport, steps) = CreateReportRunner();
        var outputs = new ExportOutputs(_root) { Producer = "extras" };
        const string path = "extras/source-report.txt";
        Directory.CreateDirectory(Path.GetDirectoryName(outputs.GetFullPath(path))!);
        await File.WriteAllTextAsync(outputs.GetFullPath(path), "old report");

        await runReport(outputs, path, () => throw new InvalidOperationException("report generation failed"));

        Assert.Equal("extras", outputs.Producer);
        Assert.Equal("old report", await File.ReadAllTextAsync(outputs.GetFullPath(path)));
        Assert.Empty(outputs.Files);
        var step = Assert.Single(steps);
        Assert.Equal(path, step.Producer);
        Assert.Equal(ExportStepStatus.Failed, step.Status);
        Assert.Equal([path], step.NotWritten);
        Assert.Empty(step.Removed);
    }

    [Fact]
    public async Task SkippedStepMakesASucceededRunIncompleteAndListsNoStaleFile()
    {
        var old = Path.Combine(_root, "keyed", "drop-calculator.json");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        await File.WriteAllTextAsync(old, "old");
        var outputs = new ExportOutputs(_root) { Producer = "a" };
        await outputs.WriteTextAsync("keyed/a.json", "{}");

        await outputs.WriteManifestAsync(succeeded: true,
        [
            new ExportStepResult("a", ExportStepStatus.Written, ["keyed/a.json"], [], [], null),
            new ExportStepResult("drop", ExportStepStatus.Skipped, [], ["keyed/drop-calculator.json"], [], "input absent")
        ], prettyPrint: false);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, ExportOutputs.ManifestFileName)));
        var root = manifest.RootElement;
        Assert.True(root.GetProperty("Succeeded").GetBoolean());
        Assert.False(root.GetProperty("Complete").GetBoolean());
        Assert.Equal("old", await File.ReadAllTextAsync(old));
        Assert.DoesNotContain(root.GetProperty("Files").EnumerateArray(),
            f => f.GetProperty("Path").GetString() == "keyed/drop-calculator.json");
        var skipped = root.GetProperty("Steps").EnumerateArray().Single(s => s.GetProperty("Producer").GetString() == "drop");
        Assert.Equal("Skipped", skipped.GetProperty("Status").GetString());
        Assert.Equal("keyed/drop-calculator.json", skipped.GetProperty("NotWritten")[0].GetString());
    }
}
