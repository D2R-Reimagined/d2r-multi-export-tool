// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using D2RMultiExport.Client.Models;
using D2RMultiExport.Client.ViewModels;
using D2RMultiExport.Client.Views;
using D2RMultiExport.Lib;
using D2RMultiExport.TestSupport;

namespace D2RMultiExport.Client.Tests;

/// <summary>
/// Real exports driven through the desktop view-model (the same command the "Import and
/// Export" button binds to), against a real mod and CASC extraction. Opt-in: set
/// <c>D2R_MOD_DATA</c> and <c>D2R_CASC_DATA</c>; skipped otherwise.
/// </summary>
public sealed class DesktopExportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-desktop-export-").FullName;

    public DesktopExportTests()
    {
        AppSettings.SettingsFile = Path.Combine(_root, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Dictionary<string, string> ManifestHashes(string output, bool requireComplete = false)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
        Assert.Equal(2, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(doc.RootElement.GetProperty("Succeeded").GetBoolean());
        if (requireComplete)
        {
            Assert.True(doc.RootElement.GetProperty("Complete").GetBoolean());
        }
        return doc.RootElement.GetProperty("Files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("Path").GetString()!, f => f.GetProperty("Sha256").GetString()!);
    }

    private static int SpriteCount(string output)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "keyed", "item-presentation.json")));
        return doc.RootElement.EnumerateArray().Count(row => row.TryGetProperty("Sprite", out _));
    }

    [AvaloniaFact]
    [Trait("Category", "RealData")]
    public async Task DesktopExportMatchesTheCliPathAndRepeatedRunsUseTheCurrentRoots()
    {
        var skipReason = RealDataInputs.SkipReason;
        if (skipReason is not null)
        {
            Assert.Skip(skipReason);
        }
        var sources = RealDataInputs.RequireSources();
        var modData = sources.ModDataRoot;
        var cascData = sources.CascDataRoot!;

        var desktopOut = Directory.CreateDirectory(Path.Combine(_root, "desktop")).FullName;
        var cliOut = RealDataInputs.SharedExport ?? Path.Combine(_root, "cli");

        var vm = new MainWindowViewModel
        {
            ModDataPath = modData,
            CascDataPath = cascData,
            OutputPath = desktopOut,
            EarlyStopSentinelEnabled = false
        };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Assert.True(vm.RunPipelineCommand.CanExecute(null), vm.ModDataStatus + " / " + vm.CascDataStatus);

        // Open the expanded log before starting, and keep it open for the whole run.
        var viewer = window.OpenLogViewer()!;
        await vm.RunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        (viewer.DataContext as LogViewerViewModel)!.Refresh();

        Assert.Equal("Export successful!", vm.StatusText);
        var log = viewer.LogText.Text ?? "";
        Assert.Contains("CASC data root:", log);
        Assert.Contains("Pipeline finished successfully.", log);
        var reportLines = File.ReadAllLines(Path.Combine(desktopOut, "extras", "import-report.txt"));
        Assert.NotEmpty(reportLines);
        // A warning-free report starts with "No errors." instead of the diagnostics header.
        Assert.Contains(reportLines[0], log);
        // Every line of the written report is in the log.
        foreach (var line in reportLines.Where(l => l.Trim().Length > 0))
            Assert.Contains(line, log);

        // The CLI's code path: the same pipeline constructed directly from the same two roots.
        var cli = new D2RMultiExportPipeline(modData, cascData, cliOut) { EarlyStopSentinelEnabled = false };
        if (RealDataInputs.SharedExport is null)
        {
            await cli.RunAsync();
            Assert.False(cli.Result.HasErrors);
        }
        else
        {
            // Produced by the library integration test in this session, with identical options.
            var sourceReport = await File.ReadAllTextAsync(Path.Combine(cliOut, "extras", "source-report.txt"));
            Assert.Contains($"Mod data root:  {sources.ModDataRoot}\n", sourceReport.Replace("\r\n", "\n"));
            Assert.Contains($"CASC data root: {sources.CascDataRoot}\n", sourceReport.Replace("\r\n", "\n"));
        }

        var desktop = ManifestHashes(desktopOut, requireComplete: true);
        var direct = ManifestHashes(cliOut, requireComplete: true);
        Assert.Equal(direct.Keys.Order(), desktop.Keys.Order());
        foreach (var (path, hash) in direct)
            Assert.True(desktop[path] == hash, $"{path} differs between desktop and CLI exports");
        Assert.Equal(cli.Sources.CascDataRoot, new D2RMultiExportPipeline(modData, cascData, desktopOut).Sources.CascDataRoot);

        // Re-run from the same window with the CASC root cleared: the new run must use the new
        // roots (no cached mapping), so vanilla-only sprites disappear from the references even
        // though the previous run's .webp files are still on disk.
        var withCasc = SpriteCount(desktopOut);
        vm.CascDataPath = "";
        await vm.RunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var withoutCasc = SpriteCount(desktopOut);
        Assert.True(withoutCasc < withCasc, $"expected fewer sprites without CASC ({withoutCasc} vs {withCasc})");
        Assert.Contains(vm.LogEntries, e => e.Text.Contains("CASC data root: (not configured"));
        var second = ManifestHashes(desktopOut);
        Assert.True(second.Count(p => p.Key.StartsWith("sprites/items/", StringComparison.Ordinal))
                    < desktop.Count(p => p.Key.StartsWith("sprites/items/", StringComparison.Ordinal)),
            "the manifest must not vouch for the previous run's vanilla sprites");
        // Asset references of the second run all point at files the second run wrote.
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(desktopOut, "keyed", "item-presentation.json"))))
        {
            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.TryGetProperty("Sprite", out var sprite))
                    Assert.True(second.ContainsKey(sprite.GetString()!), $"{sprite.GetString()} is referenced but not written by this run");
            }
        }

        // And back again: restoring the root restores the identical output.
        vm.CascDataPath = cascData;
        await vm.RunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var third = ManifestHashes(desktopOut);
        foreach (var (path, hash) in direct)
            Assert.True(third.TryGetValue(path, out var h) && h == hash, $"{path} differs after restoring the CASC root");
        Assert.True(window.IsVisible);
    }
}
