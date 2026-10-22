// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Tests;

/// <summary>
/// Installation of a finished export into a website checkout's <c>static/data</c>: only a
/// successful, complete, intact export is installed, unknown files are never deleted, stale
/// generated files disappear, and every failed swap is rolled back.
/// </summary>
public sealed class WebsiteDataInstallerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d2r-website-install-").FullName;

    public WebsiteDataInstallerTests()
    {
        Directory.CreateDirectory(Path.Combine(Site, "static"));
        File.WriteAllText(Path.Combine(Site, "package.json"), "{}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string Out => Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
    private string Site => Path.Combine(_root, "site");
    private string Static => Path.Combine(Site, "static");
    private string Data => Path.Combine(Static, "data");

    private static readonly Dictionary<string, string> V1 = new()
    {
        ["keyed/uniques.json"] = "[{\"Name\":\"Old\"}]",
        ["sprites/items/old.webp"] = "old-sprite",
        ["strings/enUS.json"] = "{\"a\":\"A\"}"
    };

    private static readonly Dictionary<string, string> V2 = new()
    {
        ["keyed/uniques.json"] = "[{\"Name\":\"New\"}]",
        ["sprites/items/new.webp"] = "new-sprite",
        ["strings/enUS.json"] = "{\"a\":\"A\"}"
    };

    private static WebsiteDataInstaller.InstallHooks FastHooks(Action<string, string>? move = null) =>
        new() { MoveDirectory = move ?? Directory.Move, RetryDelay = TimeSpan.FromMilliseconds(1) };

    /// <summary>One export run through the real output gateway, ending with its manifest.</summary>
    private async Task ExportAsync(Dictionary<string, string> files, bool succeeded = true, bool skipStep = false)
    {
        var outputs = new ExportOutputs(Out) { Producer = "keyed-core" };
        foreach (var (path, content) in files) await outputs.WriteTextAsync(path, content);
        outputs.Producer = "";
        List<ExportStepResult> steps =
        [
            new("keyed-core", ExportStepStatus.Written, [.. files.Keys], [], [], null),
            new("rare-names.json", skipStep ? ExportStepStatus.Skipped : ExportStepStatus.Written, [], [], [], skipStep ? "rareprefix.txt missing" : null)
        ];
        await outputs.WriteManifestAsync(succeeded, steps, prettyPrint: true);
    }

    private Task<WebsiteInstallResult> InstallAsync(WebsiteDataInstaller.InstallHooks? hooks = null) =>
        WebsiteDataInstaller.InstallAsync(Out, Site, hooks ?? FastHooks());

    private async Task<Dictionary<string, string>> InstallV1Async()
    {
        await ExportAsync(V1);
        var result = await InstallAsync();
        Assert.Equal(WebsiteInstallStatus.Installed, result.Status);
        return Snapshot(Data);
    }

    /// <summary>Relative path → SHA-256 of every file below <paramref name="dir"/>.</summary>
    private static Dictionary<string, string> Snapshot(string dir) =>
        !Directory.Exists(dir)
            ? []
            : Directory.GetFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
                f => Path.GetRelativePath(dir, f).Replace('\\', '/'),
                f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))));

    private static Dictionary<string, string> Expected(Dictionary<string, string> files, string manifestDir)
    {
        var expected = files.ToDictionary(f => f.Key, f => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(f.Value))));
        expected["manifest.json"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(manifestDir, "manifest.json"))));
        return expected;
    }

    private string[] StaticEntries() => [.. Directory.GetFileSystemEntries(Static).Select(Path.GetFileName).Order().OfType<string>()];

    private void EditManifest(Action<JsonObject> edit)
    {
        var path = Path.Combine(Out, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(manifest);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    [Fact]
    public async Task ACompleteExportIsInstalledWithOnlyItsOwnFilesAndAnExactManifest()
    {
        await ExportAsync(V1);
        // Not vouched for by this run: never installed, and the output directory is not touched.
        await File.WriteAllTextAsync(Path.Combine(Out, "keyed", "stale.json"), "[]");
        await File.WriteAllTextAsync(Path.Combine(Out, "notes.txt"), "mine");
        var outputBefore = Snapshot(Out);

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Installed, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(V1.Keys.Order(StringComparer.Ordinal), result.Plan!.Added);
        Assert.Equal(Expected(V1, Out), Snapshot(Data));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Out, "manifest.json")),
            await File.ReadAllBytesAsync(Path.Combine(Data, "manifest.json")));
        Assert.Equal(["data"], StaticEntries());
        Assert.Equal(outputBefore, Snapshot(Out));
    }

    [Fact]
    public async Task AnIdenticalExportLeavesTheInstallationUntouched()
    {
        var before = await InstallV1Async();
        var times = Directory.GetFiles(Data, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        await ExportAsync(V1);
        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Unchanged, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Plan!.Unchanged);
        Assert.Equal(before, Snapshot(Data));
        Assert.All(times, t => Assert.Equal(t.Value, File.GetLastWriteTimeUtc(t.Key)));
        Assert.Equal(["data"], StaticEntries());
    }

    [Fact]
    public async Task ChangedFilesAreReplacedNewOnesAddedAndStaleGeneratedFilesRemoved()
    {
        await InstallV1Async();
        // The output directory keeps v1's old.webp on disk; only the manifest decides what ships.
        await ExportAsync(V2);
        Assert.True(File.Exists(Path.Combine(Out, "sprites", "items", "old.webp")));

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Installed, result.Status);
        Assert.Equal(["sprites/items/new.webp"], result.Plan!.Added);
        Assert.Equal(["keyed/uniques.json"], result.Plan.Replaced);
        Assert.Equal(["sprites/items/old.webp"], result.Plan.Removed);
        Assert.Equal(1, result.Plan.Unchanged);
        Assert.True(result.Plan.ManifestChanged);
        Assert.Equal(Expected(V2, Out), Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task UnmanagedFilesInTheDestinationBlockTheInstallationAndAreKept()
    {
        await InstallV1Async();
        await File.WriteAllTextAsync(Path.Combine(Data, "keyed", "hand-made.json"), "{}");
        Directory.CreateDirectory(Path.Combine(Data, "empty"));
        var before = Snapshot(Data);
        await ExportAsync(V2);

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains("does not identify as generated output", result.Message);
        Assert.Contains(result.Problems, p => p.Contains("keyed/hand-made.json"));
        Assert.Contains(result.Problems, p => p.Contains("empty/ is an empty directory"));
        Assert.Equal(before, Snapshot(Data));
        Assert.True(Directory.Exists(Path.Combine(Data, "empty")));
        Assert.Equal(["data"], StaticEntries());
    }

    [Fact]
    public async Task ANonEmptyDestinationWithoutAManifestIsRefused()
    {
        Directory.CreateDirectory(Data);
        await File.WriteAllTextAsync(Path.Combine(Data, "notes.txt"), "keep me");
        await ExportAsync(V1);

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains(result.Problems, p => p.Contains("notes.txt is not listed"));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(Data, "notes.txt")));
        Assert.Single(Directory.GetFileSystemEntries(Data));
    }

    [Theory]
    [InlineData(false, false, "Succeeded is not true")]
    [InlineData(true, true, "Complete is not true")]
    public async Task FailedAndIncompleteExportsAreRefused(bool succeeded, bool skipStep, string expected)
    {
        var before = await InstallV1Async();
        await ExportAsync(V2, succeeded, skipStep);

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains(result.Problems, p => p.Contains(expected));
        if (skipStep) Assert.Contains(result.Problems, p => p.Contains("\"rare-names.json\" has status \"Skipped\""));
        Assert.Equal(before, Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
    }

    [Theory]
    [InlineData("schema", "expected 2")]
    [InlineData("malformed", "is not valid JSON")]
    [InlineData("missing", "does not exist")]
    [InlineData("failed-step", "has status \"Failed\"")]
    [InlineData("hash", "changed since the run")]
    [InlineData("bytes", "changed since the run")]
    [InlineData("tampered", "changed since the run")]
    [InlineData("listed-missing", "missing on disk")]
    [InlineData("bad-sha", "has invalid Sha256")]
    [InlineData("duplicate", "duplicates path keyed/uniques.json")]
    [InlineData("case", "collides with keyed/uniques.json")]
    [InlineData("file-dir", "needs keyed to be a directory")]
    [InlineData("escape", "contains a \".\" or \"..\" segment")]
    [InlineData("backslash", "uses a backslash separator")]
    [InlineData("device", "reserved Windows device name")]
    [InlineData("self", "names the manifest itself")]
    public async Task InvalidManifestsAndTamperedExportsLeaveTheDestinationUntouched(string kind, string expected)
    {
        var before = await InstallV1Async();
        await ExportAsync(V2);
        var manifest = Path.Combine(Out, "manifest.json");
        JsonObject CopyOfFirst(string path)
        {
            var copy = JsonNode.Parse(File.ReadAllText(manifest))!["Files"]![0]!.AsObject().DeepClone().AsObject();
            copy["Path"] = path;
            return copy;
        }
        switch (kind)
        {
            case "schema": EditManifest(m => m["SchemaVersion"] = 1); break;
            case "malformed": await File.WriteAllTextAsync(manifest, "{\"SchemaVersion\": 2,"); break;
            case "missing": File.Delete(manifest); break;
            case "failed-step": EditManifest(m => m["Steps"]![0]!["Status"] = "Failed"); break;
            case "hash": EditManifest(m => m["Files"]![0]!["Sha256"] = new string('0', 64)); break;
            case "bytes": EditManifest(m => m["Files"]![0]!["Bytes"] = m["Files"]![0]!["Bytes"]!.GetValue<long>() + 1); break;
            case "tampered": await File.WriteAllTextAsync(Path.Combine(Out, "keyed", "uniques.json"), "[\"edited\"]"); break;
            case "listed-missing": File.Delete(Path.Combine(Out, "sprites", "items", "new.webp")); break;
            case "bad-sha": EditManifest(m => m["Files"]![0]!["Sha256"] = "ABC"); break;
            case "duplicate": { var c = CopyOfFirst("keyed/uniques.json"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
            case "case": { var c = CopyOfFirst("KEYED/Uniques.json"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
            case "file-dir": { var c = CopyOfFirst("Keyed"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
            case "escape": { var c = CopyOfFirst("keyed/../../escape.json"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
            case "backslash": { var c = CopyOfFirst("keyed\\x.json"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
            case "device": { var c = CopyOfFirst("keyed/con.json"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
            case "self": { var c = CopyOfFirst("Manifest.JSON"); EditManifest(m => m["Files"]!.AsArray().Add(c)); break; }
        }

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Message + "\n" + string.Join("\n", result.Problems));
        Assert.Equal(before, Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("keyed/../../escape.json")]
    [InlineData("..\\escape.json")]
    [InlineData("\\\\server\\share\\x.json")]
    [InlineData("/etc/escape.json")]
    [InlineData("C:/Windows/escape.json")]
    [InlineData("c:escape.json")]
    [InlineData("keyed//double.json")]
    [InlineData("./keyed/dot.json")]
    [InlineData("keyed/trailing.")]
    [InlineData("keyed/trailing ")]
    [InlineData("keyed/con.json")]
    [InlineData("keyed/LPT1")]
    [InlineData("keyed/stream.json:hidden")]
    [InlineData("keyed/ctrl\u0001.json")]
    [InlineData("manifest.json")]
    [InlineData("Manifest.JSON")]
    [InlineData("")]
    public void UnsafeOrUnportablePathsAreRejected(string path)
    {
        Assert.NotNull(WebsiteDataInstaller.PathProblem(path));
    }

    [Theory]
    [InlineData("sprites/skills/ama-0.webp")]
    [InlineData("extras/asset-report.txt")]
    [InlineData("keyed/console.json")]
    public void OrdinaryNestedPathsAreAccepted(string path)
    {
        Assert.Null(WebsiteDataInstaller.PathProblem(path));
    }

    [Fact]
    public async Task AFailedPromotionRestoresTheBackup()
    {
        var before = await InstallV1Async();
        await ExportAsync(V2);
        var hooks = FastHooks((from, to) =>
        {
            if (Path.GetFileName(from).StartsWith(".data.install-", StringComparison.Ordinal))
                throw new IOException("simulated promotion failure");
            Directory.Move(from, to);
        });

        var result = await InstallAsync(hooks);

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains("restored from its backup", result.Message);
        Assert.Equal(before, Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
    }

    [Fact]
    public async Task ALockedDestinationIsRetriedAndThenLeftUnchanged()
    {
        var before = await InstallV1Async();
        await ExportAsync(V2);
        var attempts = 0;
        var hooks = FastHooks((_, _) =>
        {
            attempts++;
            throw new UnauthorizedAccessException("locked");
        });

        var result = await InstallAsync(hooks);

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains("not changed", result.Message);
        Assert.Equal(6, attempts);
        Assert.Equal(before, Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
    }

    [Fact]
    public async Task AFileHeldOpenInTheDestinationIsReportedWithoutDamage()
    {
        if (!OperatingSystem.IsWindows()) return; // open handles block directory renames on Windows only
        var before = await InstallV1Async();
        await ExportAsync(V2);

        WebsiteInstallResult result;
        await using (new FileStream(Path.Combine(Data, "keyed", "uniques.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await InstallAsync();
        }

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains("not changed", result.Message);
        Assert.Equal(before, Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
    }

    [Fact]
    public async Task AnInstallationFailingVerificationIsRolledBack()
    {
        var before = await InstallV1Async();
        await ExportAsync(V2);
        var hooks = FastHooks((from, to) =>
        {
            Directory.Move(from, to);
            // Something changes the promoted copy before it is verified.
            if (Path.GetFileName(from).StartsWith(".data.install-", StringComparison.Ordinal))
                File.WriteAllText(Path.Combine(to, "keyed", "uniques.json"), "[{\"Name\":\"Bad\"}]"); // same length, other hash
        });

        var result = await InstallAsync(hooks);

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains("the previous data was restored", result.Message);
        Assert.Contains(result.Problems, p => p.Contains("keyed/uniques.json SHA-256"));
        Assert.Equal(before, Snapshot(Data));
        Assert.Equal(["data"], StaticEntries());
    }

    [Fact]
    public async Task AnInterruptedInstallationIsRecoveredFromItsBackupOnTheNextExport()
    {
        var before = await InstallV1Async();
        await ExportAsync(V2);
        // Promotion and the automatic restore both fail: the state a killed process leaves.
        var hooks = FastHooks((from, to) =>
        {
            if (!Path.GetFileName(to).StartsWith(".data.backup-", StringComparison.Ordinal))
                throw new IOException("simulated crash");
            Directory.Move(from, to);
        });
        var interrupted = await InstallAsync(hooks);
        Assert.Equal(WebsiteInstallStatus.Refused, interrupted.Status);
        Assert.Contains("by hand", interrupted.Message);
        Assert.False(Directory.Exists(Data));
        var backup = Assert.Single(Directory.GetDirectories(Static, ".data.backup-*"));
        Assert.Equal(before, Snapshot(backup));

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Installed, result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("Restored"));
        Assert.Contains(result.Warnings, w => w.Contains(".data.install-"));
        Assert.Equal(["sprites/items/old.webp"], result.Plan!.Removed);
        Assert.Equal(Expected(V2, Out), Snapshot(Data));
        Assert.Empty(Directory.GetDirectories(Static, ".data.backup-*"));
    }

    [Fact]
    public async Task ABackupBesideExistingDataBlocksTheInstallation()
    {
        var before = await InstallV1Async();
        var earlier = Directory.CreateDirectory(Path.Combine(Static, ".data.backup-earlier")).FullName;
        await File.WriteAllTextAsync(Path.Combine(earlier, "keep.json"), "{}");
        await ExportAsync(V2);

        var result = await InstallAsync();

        Assert.Equal(WebsiteInstallStatus.Refused, result.Status);
        Assert.Contains("interrupted", result.Message);
        Assert.Equal(before, Snapshot(Data));
        Assert.True(File.Exists(Path.Combine(earlier, "keep.json")));
    }

    [Fact]
    public async Task InvalidWebsiteRootsAndNestedOutputsAreRefused()
    {
        await ExportAsync(V1);
        var notAWebsite = Directory.CreateDirectory(Path.Combine(_root, "random", "static")).Parent!.FullName;
        var invalid = await WebsiteDataInstaller.InstallAsync(Out, notAWebsite);
        Assert.Equal(WebsiteInstallStatus.Refused, invalid.Status);
        Assert.Contains("package.json", invalid.Message);
        Assert.False(Directory.Exists(Path.Combine(notAWebsite, "static", "data")));

        Assert.True(WebsiteDataInstaller.OverlapsDestination(Data, Site));
        Assert.True(WebsiteDataInstaller.OverlapsDestination(Path.Combine(Data, "out"), Site));
        Assert.False(WebsiteDataInstaller.OverlapsDestination(Out, Site));
        var nested = await WebsiteDataInstaller.InstallAsync(Static, Site);
        Assert.Equal(WebsiteInstallStatus.Refused, nested.Status);
        Assert.Contains("must not contain each other", nested.Message);
        Assert.False(Directory.Exists(Data));
    }
}
