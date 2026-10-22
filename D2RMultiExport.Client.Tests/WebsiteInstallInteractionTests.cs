// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using D2RMultiExport.Client.Models;
using D2RMultiExport.Client.ViewModels;
using D2RMultiExport.Client.Views;

namespace D2RMultiExport.Client.Tests;

/// <summary>
/// The optional website directory in the desktop window: remembered in settings, validated
/// as typed, and — once configured — updated by the normal "Import and Export" run without
/// any further action. Exports are simulated through <c>RunOverride</c>; the installation is real.
/// </summary>
public sealed class WebsiteInstallInteractionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-client-website-").FullName;

    public WebsiteInstallInteractionTests()
    {
        AppSettings.SettingsFile = Path.Combine(_root, "settings.json");
        Directory.CreateDirectory(Path.Combine(Mod, "global", "excel"));
        Directory.CreateDirectory(Path.Combine(Mod, "local", "lng", "strings"));
        Directory.CreateDirectory(Output);
        Directory.CreateDirectory(Path.Combine(Website, "static"));
        File.WriteAllText(Path.Combine(Website, "package.json"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Mod => Path.Combine(_root, "mod", "data");
    private string Output => Path.Combine(_root, "out");
    private string Website => Path.Combine(_root, "website");
    private string Data => Path.Combine(Website, "static", "data");

    /// <summary>Writes payload files plus a schema-2 manifest, as a finished export run does.</summary>
    private void WriteExport(Dictionary<string, string> files, bool complete = true)
    {
        var entries = new List<object>();
        foreach (var (path, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var full = Path.Combine(Output, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var bytes = Encoding.UTF8.GetBytes(content);
            File.WriteAllBytes(full, bytes);
            entries.Add(new { Path = path, Bytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Producer = "test" });
        }
        var manifest = new
        {
            SchemaVersion = 2,
            Succeeded = true,
            Complete = complete,
            AbandonedTempFilesRemoved = 0,
            Steps = new object[]
            {
                new { Producer = "test", Status = "Written", Outputs = files.Keys.ToArray() },
                new { Producer = "rare-names.json", Status = complete ? "Written" : "Skipped", Outputs = Array.Empty<string>() }
            },
            Files = entries
        };
        File.WriteAllText(Path.Combine(Output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, string> Snapshot(string dir) =>
        !Directory.Exists(dir)
            ? []
            : Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
                File.ReadAllText);

    private (MainWindow Window, MainWindowViewModel Vm) ShowMain()
    {
        var vm = new MainWindowViewModel { ModDataPath = Mod, OutputPath = Output };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static async Task RunAsync(MainWindowViewModel vm)
    {
        Assert.True(vm.RunPipelineCommand.CanExecute(null), vm.ModDataStatus + " / " + vm.WebsiteStatus);
        await vm.RunPipelineCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task AConfiguredWebsiteIsUpdatedByTheNormalExportAndRemembered()
    {
        var (window, vm) = ShowMain();
        window.WebsiteBox.Text = Website; // typed into the field, as the folder picker would set it
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Website, vm.WebsiteRepositoryPath);
        Assert.True(vm.WebsiteValid, vm.WebsiteStatus);
        Assert.Contains(Path.Combine("static", "data"), window.WebsiteStatusText.Text);

        var version = 1;
        vm.RunOverride = _ =>
        {
            WriteExport(version == 1
                ? new() { ["keyed/uniques.json"] = "[1]", ["sprites/items/old.webp"] = "old" }
                : new() { ["keyed/uniques.json"] = "[2]", ["sprites/items/new.webp"] = "new" });
            return Task.CompletedTask;
        };

        await RunAsync(vm);
        Assert.StartsWith("Export successful! Website data installed:", vm.StatusText);
        Assert.Equal("[1]", Snapshot(Data)["keyed/uniques.json"]);
        Assert.Equal(Website, AppSettings.Load().WebsiteRepositoryPath);

        // The next export replaces the previous generation, including its stale sprite.
        version = 2;
        await RunAsync(vm);
        Assert.StartsWith("Export successful! Website data installed:", vm.StatusText);
        Assert.Contains("1 added, 1 replaced, 1 removed", vm.StatusText);
        Assert.Equal(["keyed/uniques.json", "manifest.json", "sprites/items/new.webp"], Snapshot(Data).Keys.Order(StringComparer.Ordinal));
        Assert.Contains(vm.LogEntries, e => e.Text == "Website: removed sprites/items/old.webp");

        // An identical export changes nothing and says so.
        await RunAsync(vm);
        Assert.StartsWith("Export successful! Website data is already up to date:", vm.StatusText);

        // A new window restores the remembered website directory.
        var reopened = new MainWindowViewModel();
        Assert.Equal(Website, reopened.WebsiteRepositoryPath);
        Assert.True(reopened.WebsiteValid);
    }

    [AvaloniaFact]
    public async Task ARefusedInstallationIsReportedAndLeavesTheWebsiteUntouched()
    {
        var (_, vm) = ShowMain();
        vm.WebsiteRepositoryPath = Website;
        vm.RunOverride = _ =>
        {
            WriteExport(new() { ["keyed/uniques.json"] = "[1]" });
            return Task.CompletedTask;
        };
        await RunAsync(vm);
        var before = Snapshot(Data);

        vm.RunOverride = _ =>
        {
            WriteExport(new() { ["keyed/uniques.json"] = "[2]" }, complete: false);
            return Task.CompletedTask;
        };
        await RunAsync(vm);

        Assert.StartsWith("Export successful, but website installation was refused:", vm.StatusText);
        Assert.Contains(vm.LogEntries, e => e.Level == LogLevel.Error && e.Text.Contains("Website installation refused"));
        Assert.Contains(vm.LogEntries, e => e.Level == LogLevel.Error && e.Text.Contains("Complete is not true"));
        Assert.Equal(before, Snapshot(Data));
        Assert.True(vm.RunPipelineCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task AnExportWithErrorsDoesNotTouchTheWebsite()
    {
        var (_, vm) = ShowMain();
        vm.WebsiteRepositoryPath = Website;
        vm.RunOverride = pipeline =>
        {
            WriteExport(new() { ["keyed/uniques.json"] = "[1]" });
            pipeline.Result.AddError("Export", "uniques.json", "simulated failure");
            return Task.CompletedTask;
        };

        await RunAsync(vm);

        Assert.StartsWith("Export finished with 1 issue(s)", vm.StatusText);
        Assert.EndsWith("Website data was not updated.", vm.StatusText);
        Assert.False(Directory.Exists(Data));
    }

    [AvaloniaFact]
    public async Task WithoutAWebsiteTheExportBehavesAsBefore()
    {
        var (_, vm) = ShowMain();
        Assert.True(vm.WebsiteValid);
        Assert.StartsWith("Optional:", vm.WebsiteStatus);
        vm.RunOverride = _ =>
        {
            WriteExport(new() { ["keyed/uniques.json"] = "[1]" });
            return Task.CompletedTask;
        };

        await RunAsync(vm);

        Assert.Equal("Export successful!", vm.StatusText);
        Assert.DoesNotContain(vm.LogEntries, e => e.Text.StartsWith("Website", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Data));
    }

    [AvaloniaFact]
    public void InvalidWebsiteSelectionsAreExplainedAndBlockTheRun()
    {
        var (window, vm) = ShowMain();
        Assert.True(vm.RunPipelineCommand.CanExecute(null));

        vm.WebsiteRepositoryPath = Path.Combine(_root, "missing");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.WebsiteValid);
        Assert.Contains("does not exist", window.WebsiteStatusText.Text);
        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        vm.WebsiteRepositoryPath = Path.Combine(Website, "static");
        Assert.False(vm.WebsiteValid);
        Assert.Contains("package.json", vm.WebsiteStatus);

        // Exporting straight into the website's data directory conflicts with installing there.
        vm.WebsiteRepositoryPath = Website;
        Directory.CreateDirectory(Data);
        vm.OutputPath = Data;
        Assert.False(vm.WebsiteValid);
        Assert.Contains("outside", vm.WebsiteStatus);
        Assert.False(vm.RunPipelineCommand.CanExecute(null));

        vm.OutputPath = Output;
        Assert.True(vm.WebsiteValid, vm.WebsiteStatus);
        vm.WebsiteRepositoryPath = "";
        Assert.True(vm.WebsiteValid);
        Assert.True(vm.RunPipelineCommand.CanExecute(null));
    }
}
