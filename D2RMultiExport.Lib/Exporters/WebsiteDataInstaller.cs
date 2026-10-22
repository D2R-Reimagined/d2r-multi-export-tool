// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Outcome of <see cref="WebsiteDataInstaller.InstallAsync(string, string)"/>.</summary>
public enum WebsiteInstallStatus
{
    /// <summary>The export was installed and verified in the website's <c>static/data</c>.</summary>
    Installed,

    /// <summary>The website already holds exactly this export; nothing was written.</summary>
    Unchanged,

    /// <summary>Nothing was installed. The message says whether the destination was left untouched or restored.</summary>
    Refused
}

/// <summary>Differences between the installed data and the export, by manifest-relative path.</summary>
/// <param name="Added">Files the destination did not have.</param>
/// <param name="Replaced">Files whose content changed.</param>
/// <param name="Removed">Previously generated files the new manifest no longer lists.</param>
/// <param name="Unchanged">Number of files with identical content.</param>
/// <param name="ManifestChanged">False when the installed manifest is byte-for-byte the export's.</param>
public sealed record WebsiteInstallPlan(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Removed,
    int Unchanged,
    bool ManifestChanged)
{
    /// <summary>One-line count summary, e.g. <c>2 added, 1 replaced, 1 removed, 880 unchanged</c>.</summary>
    public string Summary => $"{Added.Count} added, {Replaced.Count} replaced, {Removed.Count} removed, {Unchanged} unchanged";
}

/// <summary>Result of a website data installation.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Destination">The website's <c>static/data</c> directory.</param>
/// <param name="Message">Human-readable outcome, including the state the destination was left in.</param>
/// <param name="Plan">Changes relative to the previous installation, once the destination was inspected.</param>
/// <param name="Problems">Why installation was refused, or what failed verification.</param>
/// <param name="Warnings">Non-fatal notes (leftover working directories, an undeletable backup).</param>
public sealed record WebsiteInstallResult(
    WebsiteInstallStatus Status,
    string Destination,
    string Message,
    WebsiteInstallPlan? Plan,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when the website now holds exactly the export.</summary>
    public bool Succeeded => Status != WebsiteInstallStatus.Refused;
}

/// <summary>
/// Replaces a website checkout's <c>static/data</c> with the export a successful run just
/// wrote. The export directory's ownership rules are unchanged: its manifest is captured and
/// exactly its <c>Files[]</c> are copied by <see cref="PublishStager"/> into a sibling working
/// directory, never the whole output directory. The installation then
/// <list type="bullet">
/// <item><description>refuses anything but a schema-2 manifest with <c>Succeeded</c>,
/// <c>Complete</c> and every step <c>Written</c> (warnings do not affect these);</description></item>
/// <item><description>rejects unsafe, unportable, duplicate, case-colliding or file/directory
/// conflicting paths, and a prepared copy holding anything unlisted, a link, a different
/// size/hash, or a manifest that is not the captured bytes;</description></item>
/// <item><description>refuses a destination holding anything its current manifest does not
/// list as generated output, so unknown files are never deleted;</description></item>
/// <item><description>leaves an identical installation alone, otherwise swaps directories
/// (<c>data</c> → backup, prepared → <c>data</c>), verifies, and deletes the backup. A failed
/// rename or verification restores the backup; locked directories are retried.</description></item>
/// </list>
/// Working directories are named <c>.data.install-*</c>, <c>.data.backup-*</c> and
/// <c>.data.failed-*</c> beside <c>static/data</c>, matching the website's own installer and
/// its <c>.gitignore</c>. A leftover backup means an interrupted installation: when
/// <c>static/data</c> is missing the single backup is restored first; otherwise installation
/// is refused until it is resolved by hand. The two renames are not one atomic transaction.
/// </summary>
public static class WebsiteDataInstaller
{
    private const string Manifest = ExportOutputs.ManifestFileName;

    private static readonly Regex Sha256Shape = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>Characters Windows rejects in a file name, plus control characters.</summary>
    private static readonly Regex InvalidSegmentChars = new(@"[<>:""|?*\\\u0000-\u001f\u007f]", RegexOptions.CultureInvariant);

    /// <summary>Reserved Windows device names, with or without an extension.</summary>
    private static readonly Regex WindowsDevice = new(@"^(con|prn|aux|nul|conin\$|conout\$|com[0-9¹²³]|lpt[0-9¹²³])(\..*)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private const int MoveRetries = 5;

    /// <summary>The <c>static/data</c> directory of a website checkout.</summary>
    public static string DestinationFor(string websiteRoot) =>
        Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(websiteRoot)), "static", "data");

    /// <summary>
    /// Checks that <paramref name="websiteRoot"/> is a website checkout: an existing directory
    /// with <c>package.json</c> and <c>static/</c>. <c>static/data</c> itself may be absent.
    /// </summary>
    public static (bool IsValid, string Message) ValidateWebsiteRoot(string websiteRoot)
    {
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(websiteRoot));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return (false, $"'{websiteRoot}' is not a valid path: {ex.Message}");
        }
        if (!Directory.Exists(root))
            return (false, $"'{root}' does not exist.");
        if (!File.Exists(Path.Combine(root, "package.json")) || !Directory.Exists(Path.Combine(root, "static")))
            return (false, $"'{root}' is not a website checkout: select the repository root that contains package.json and static{Path.DirectorySeparatorChar}.");
        return (true, $"Successful, complete exports replace '{DestinationFor(root)}'.");
    }

    /// <summary>True when the export directory and the website's <c>static/data</c> contain each other.</summary>
    public static bool OverlapsDestination(string outputRoot, string websiteRoot)
    {
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var target = DestinationFor(websiteRoot);
        return RealPath.IsUnder(output, target) || RealPath.IsUnder(target, output);
    }

    /// <summary>Installs the export in <paramref name="outputRoot"/> into <c>&lt;websiteRoot&gt;/static/data</c>.</summary>
    public static Task<WebsiteInstallResult> InstallAsync(string outputRoot, string websiteRoot)
        => InstallAsync(outputRoot, websiteRoot, new InstallHooks());

    /// <summary>Deterministic regression seams for rename failures and retry timing.</summary>
    internal sealed class InstallHooks
    {
        public Action<string, string> MoveDirectory { get; init; } = Directory.Move;
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    }

    internal static async Task<WebsiteInstallResult> InstallAsync(string outputRoot, string websiteRoot, InstallHooks hooks)
    {
        var warnings = new List<string>();
        try
        {
            return await InstallCoreAsync(outputRoot, websiteRoot, hooks, warnings);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            // Every step after the first rename handles its own failures, so this is a read or
            // preparation failure that happened while the destination was still untouched.
            var target = DestinationFor(websiteRoot);
            return new WebsiteInstallResult(WebsiteInstallStatus.Refused, target,
                $"Installation failed before '{target}' was changed: {ex.Message}", null, [], warnings);
        }
    }

    private static async Task<WebsiteInstallResult> InstallCoreAsync(string outputRoot, string websiteRoot, InstallHooks hooks, List<string> warnings)
    {
        var target = DestinationFor(websiteRoot);
        var (validRoot, rootMessage) = ValidateWebsiteRoot(websiteRoot);
        if (!validRoot) return Refuse(rootMessage);

        var parent = Path.GetDirectoryName(target)!;
        var realParent = RealPath.OfDirectory(parent);
        if (RealPath.IsSupported && realParent is null)
            return Refuse($"Could not resolve the physical location of '{parent}'.");
        target = Path.Combine(realParent ?? parent, Path.GetFileName(target));
        parent = Path.GetDirectoryName(target)!;

        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        output = RealPath.OfDirectory(output) ?? output;
        if (RealPath.IsUnder(output, target) || RealPath.IsUnder(target, output))
            return Refuse($"The export directory '{output}' and the website data directory '{target}' must not contain each other. "
                          + "Export to a separate directory; the website copy is installed from it.");

        // Validated before anything is written, so a refusal costs no copying.
        var manifestPath = Path.Combine(output, Manifest);
        if (!File.Exists(manifestPath))
            return Refuse($"'{manifestPath}' does not exist: the export did not finish its output phase.");
        var exported = ReadBundle(await File.ReadAllBytesAsync(manifestPath), manifestPath, out var manifestProblems);
        if (exported is null) return Refuse($"The export manifest '{manifestPath}' cannot be installed.", manifestProblems);

        var prefix = "." + Path.GetFileName(target) + ".";
        var leftovers = Directory.EnumerateFileSystemEntries(parent, prefix + "*")
            .Select(Path.GetFileName).OfType<string>()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        var backups = leftovers.Where(name => name.StartsWith(prefix + "backup-", StringComparison.Ordinal)).ToList();
        if (backups.Count == 1 && !Path.Exists(target))
        {
            // Killed between "data -> backup" and "prepared -> data": the backup is the previous data.
            var restored = Path.Combine(parent, backups[0]);
            try
            {
                await MoveWithRetryAsync(hooks, restored, target);
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                return Refuse($"A previous installation was interrupted and its backup '{restored}' could not be restored ({ex.Message}). "
                              + $"Rename it to '{target}' by hand.");
            }
            warnings.Add($"Restored '{target}' from the backup of an interrupted installation ('{restored}').");
            leftovers.Remove(backups[0]);
            backups.Clear();
        }
        if (backups.Count > 0)
            return Refuse($"A previous installation was interrupted. Inspect these directories in '{parent}' and restore or delete them before exporting again; nothing was changed.",
                backups);
        warnings.AddRange(leftovers.Select(name => $"Leftover directory '{Path.Combine(parent, name)}' from an earlier installation can be deleted."));

        Destination destination;
        try
        {
            destination = InspectDestination(target);
        }
        catch (InvalidOperationException ex)
        {
            return Refuse(ex.Message);
        }
        if (destination.Unrecognized.Count > 0)
            return Refuse($"'{target}' contains entries its manifest does not identify as generated output. Move or remove them manually; nothing was changed.",
                destination.Unrecognized);

        var plan = await PlanAsync(exported, destination, target);
        if (destination.Exists && plan.Added.Count == 0 && plan.Replaced.Count == 0 && plan.Removed.Count == 0 && !plan.ManifestChanged)
            return new WebsiteInstallResult(WebsiteInstallStatus.Unchanged, target,
                $"'{target}' already holds this export; nothing was changed.", plan, [], warnings);

        var stamp = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
        var prepared = Path.Combine(parent, $"{prefix}install-{stamp}");
        var backup = Path.Combine(parent, $"{prefix}backup-{stamp}");
        var failed = Path.Combine(parent, $"{prefix}failed-{stamp}");

        // PublishStager copies exactly Files[] with per-byte hash verification against the
        // manifest it captures, so a concurrent export can never yield a mixed copy.
        var staged = await PublishStager.StageAsync(output, prepared, allowIncomplete: false);
        if (!staged.Staged)
        {
            RemoveQuietly(prepared);
            return Refuse($"Preparing the replacement failed; '{target}' was not changed.", staged.Problems, plan);
        }
        var preparedRaw = await File.ReadAllBytesAsync(Path.Combine(prepared, Manifest));
        if (!preparedRaw.AsSpan().SequenceEqual(exported.Raw))
        {
            RemoveQuietly(prepared);
            return Refuse($"The export changed while it was being installed; '{target}' was not changed. Export again.", [], plan);
        }
        var preparedProblems = await VerifyCopyAsync(prepared, exported);
        if (preparedProblems.Count > 0)
        {
            RemoveQuietly(prepared);
            return Refuse($"The prepared copy does not match the export manifest; '{target}' was not changed.", preparedProblems, plan);
        }

        var backedUp = false;
        try
        {
            if (destination.Exists)
            {
                await MoveWithRetryAsync(hooks, target, backup);
                backedUp = true;
            }
            await MoveWithRetryAsync(hooks, prepared, target);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            if (backedUp)
            {
                try
                {
                    await MoveWithRetryAsync(hooks, backup, target);
                }
                catch (Exception restoreError) when (IsIoFailure(restoreError))
                {
                    return Refuse($"Replacing '{target}' failed ({ex.Message}) and the previous data could not be restored automatically "
                                  + $"({restoreError.Message}). Rename '{backup}' to '{target}' by hand. The verified replacement is in '{prepared}'.",
                        [], plan);
                }
            }
            RemoveQuietly(prepared);
            return Refuse($"Replacing '{target}' failed ({ex.Message}); it was {(backedUp ? "restored from its backup" : "not changed")}. "
                          + "Close programs using it (for example the website dev server) and export again.", [], plan);
        }

        var installedProblems = await VerifyCopyAsync(target, exported);
        if (installedProblems.Count > 0)
        {
            try
            {
                await MoveWithRetryAsync(hooks, target, failed);
                if (backedUp) await MoveWithRetryAsync(hooks, backup, target);
            }
            catch (Exception restoreError) when (IsIoFailure(restoreError))
            {
                return Refuse($"The installed data failed verification and could not be rolled back ({restoreError.Message}). "
                              + $"Previous data: {(backedUp ? $"'{backup}'" : "none")}. Failed installation: '{failed}'.", installedProblems, plan);
            }
            RemoveQuietly(failed);
            return Refuse($"The installed data failed verification; {(backedUp ? "the previous data was restored" : "it was removed")}.",
                installedProblems, plan);
        }

        if (backedUp)
        {
            // Re-inspected: anything that appeared after the first inspection is kept, not deleted.
            List<string> late;
            try
            {
                late = InspectDestination(backup).Unrecognized;
            }
            catch (Exception ex) when (IsIoFailure(ex) || ex is InvalidOperationException)
            {
                late = [$"it could not be inspected ({ex.Message})"];
            }
            if (late.Count > 0)
            {
                warnings.Add($"Installed successfully, but the backup '{backup}' was kept because it contains files that are not generated output: {string.Join(", ", late)}.");
            }
            else
            {
                try
                {
                    Directory.Delete(backup, recursive: true);
                }
                catch (Exception ex) when (IsIoFailure(ex))
                {
                    warnings.Add($"Installed successfully, but the backup '{backup}' could not be deleted: {ex.Message}");
                }
            }
        }
        return new WebsiteInstallResult(WebsiteInstallStatus.Installed, target,
            $"Installed and verified '{target}' ({plan.Summary}).", plan, [], warnings);

        WebsiteInstallResult Refuse(string message, IReadOnlyList<string>? problems = null, WebsiteInstallPlan? refusedPlan = null) =>
            new(WebsiteInstallStatus.Refused, target, message, refusedPlan, problems ?? [], warnings);
    }

    /// <summary>
    /// Why a manifest path is unsafe or unportable, or <c>null</c> for a plain root-relative
    /// <c>/</c> path that is valid on Windows and case-insensitive file systems.
    /// </summary>
    internal static string? PathProblem(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "path is empty or not a string";
        if (path.Contains('\\')) return "uses a backslash separator";
        if (path.StartsWith('/')) return "is absolute";
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return "has a drive prefix";
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0) return "has an empty segment";
            if (segment is "." or "..") return "contains a \".\" or \"..\" segment";
            if (InvalidSegmentChars.IsMatch(segment)) return $"segment \"{segment}\" contains a character invalid in Windows names";
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return $"segment \"{segment}\" ends with a dot or space";
            if (WindowsDevice.IsMatch(segment)) return $"segment \"{segment}\" is a reserved Windows device name";
        }
        if (Fold(path) == Manifest) return "names the manifest itself";
        return null;
    }

    /// <summary>Windows compares names case-insensitively; macOS also normalizes Unicode. Fold both.</summary>
    private static string Fold(string path) => path.Normalize(NormalizationForm.FormC).ToLowerInvariant();

    private sealed record Entry(long Bytes, string Sha256);

    private sealed record Bundle(byte[] Raw, IReadOnlyDictionary<string, Entry> Files);

    /// <summary>Manifest-level and Files[] checks; source and calculation validation belong to the export.</summary>
    private static Bundle? ReadBundle(byte[] raw, string manifestPath, out List<string> problems)
    {
        problems = [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("manifest is not a JSON object");
                return null;
            }
            if (!root.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var version) || version != ExportOutputs.ManifestSchemaVersion)
                problems.Add($"SchemaVersion is {Describe(root, "SchemaVersion")}, expected {ExportOutputs.ManifestSchemaVersion}");
            if (!IsTrue(root, "Succeeded")) problems.Add("Succeeded is not true: the export recorded errors");
            if (!IsTrue(root, "Complete")) problems.Add("Complete is not true: the export skipped or did not write declared outputs");
            if (!root.TryGetProperty("Steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            {
                problems.Add("Steps is not an array");
            }
            else
            {
                foreach (var step in steps.EnumerateArray())
                {
                    var producer = Describe(step, "Producer");
                    if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("Status", out var status)
                        || status.ValueKind != JsonValueKind.String || status.GetString() != nameof(ExportStepStatus.Written))
                        problems.Add($"step {producer} has status {Describe(step, "Status")}");
                    else if (step.TryGetProperty("NotWritten", out var notWritten) && notWritten.ValueKind == JsonValueKind.Array
                             && notWritten.GetArrayLength() > 0)
                        problems.Add($"step {producer} did not write {notWritten.GetArrayLength()} declared output(s)");
                }
            }
            if (!root.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                problems.Add("Files is not an array");
                return null;
            }
            if (problems.Count > 0) return null;

            var entries = EntryProblems(files, problems);
            return problems.Count > 0 ? null : new Bundle(raw, entries);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            problems.Add($"'{manifestPath}' is not valid JSON: {ex.Message}");
            return null;
        }
    }

    /// <summary>Path, size and hash shape of Files[], plus duplicates, case collisions and file/directory clashes.</summary>
    private static Dictionary<string, Entry> EntryProblems(JsonElement files, List<string> problems)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var folded = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in files.EnumerateArray())
        {
            var label = $"Files[{index++}]";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label} is not an object");
                continue;
            }
            var path = entry.TryGetProperty("Path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var problem = PathProblem(path);
            if (problem is not null)
            {
                problems.Add($"{label} {Describe(entry, "Path")} {problem}");
                continue;
            }
            if (!entry.TryGetProperty("Bytes", out var b) || b.ValueKind != JsonValueKind.Number || !b.TryGetInt64(out var bytes) || bytes < 0)
            {
                problems.Add($"{label} {path} has invalid Bytes {Describe(entry, "Bytes")}");
                bytes = -1;
            }
            var sha = entry.TryGetProperty("Sha256", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (sha is null || !Sha256Shape.IsMatch(sha))
                problems.Add($"{label} {path} has invalid Sha256 {Describe(entry, "Sha256")}");
            var key = Fold(path!);
            if (folded.TryGetValue(key, out var previous))
            {
                problems.Add(previous == path
                    ? $"{label} duplicates path {path}"
                    : $"{label} {path} collides with {previous} on case-insensitive file systems");
                continue;
            }
            folded[key] = path!;
            entries[path!] = new Entry(bytes, sha ?? "");
        }
        foreach (var file in entries.Keys)
        {
            foreach (var dir in Ancestors(file))
            {
                if (folded.TryGetValue(Fold(dir), out var clash))
                    problems.Add($"{file} needs {dir} to be a directory, but {clash} is listed as a file");
            }
        }
        return entries;
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Describe(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.GetRawText() : "missing";

    private static IEnumerable<string> Ancestors(string relative)
    {
        for (var i = relative.IndexOf('/'); i >= 0; i = relative.IndexOf('/', i + 1))
            yield return relative[..i];
    }

    private static string ToAbsolute(string root, string relative) => Path.Combine([root, .. relative.Split('/')]);

    private sealed record Inventory(Dictionary<string, long> Files, List<string> Unsafe, List<string> EmptyDirectories);

    /// <summary>Every entry below <paramref name="root"/>. Links and junctions are reported, never followed.</summary>
    private static Inventory Walk(string root)
    {
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var directories = new List<string>();
        var unsafeEntries = new List<string>();
        Visit("");
        var occupied = files.Keys.SelectMany(Ancestors).ToHashSet(StringComparer.Ordinal);
        return new Inventory(files, unsafeEntries, [.. directories.Where(d => !occupied.Contains(d))]);

        void Visit(string relative)
        {
            var directory = new DirectoryInfo(relative.Length == 0 ? root : ToAbsolute(root, relative));
            foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(static e => e.Name, StringComparer.Ordinal))
            {
                var child = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
                if (entry.LinkTarget is not null)
                {
                    unsafeEntries.Add($"{child} is a symbolic link or junction");
                }
                else if (entry is DirectoryInfo)
                {
                    directories.Add(child);
                    Visit(child);
                }
                else if (entry is FileInfo file)
                {
                    files[child] = file.Length;
                }
            }
        }
    }

    private static async Task<(long Bytes, string Sha256)> HashAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var hash = await SHA256.HashDataAsync(stream);
        return (stream.Length, Convert.ToHexStringLower(hash));
    }

    /// <summary>Exactly the listed files, with matching sizes and hashes, and nothing else.</summary>
    private static async Task<List<string>> ContentProblemsAsync(string root, Bundle bundle)
    {
        Inventory found;
        try
        {
            found = Walk(root);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return [$"'{root}' could not be read: {ex.Message}"];
        }
        var problems = new List<string>(found.Unsafe);
        foreach (var (file, expected) in bundle.Files)
        {
            if (!found.Files.ContainsKey(file))
            {
                problems.Add($"{file} is listed but missing");
                continue;
            }
            (long Bytes, string Sha256) actual;
            try
            {
                actual = await HashAsync(ToAbsolute(root, file));
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                problems.Add($"{file} could not be read: {ex.Message}");
                continue;
            }
            if (actual.Bytes != expected.Bytes)
                problems.Add($"{file} is {actual.Bytes} bytes, manifest declares {expected.Bytes}");
            else if (actual.Sha256 != expected.Sha256)
                problems.Add($"{file} SHA-256 {actual.Sha256} does not match manifest {expected.Sha256}");
        }
        problems.AddRange(found.Files.Keys.Where(f => f != Manifest && !bundle.Files.ContainsKey(f)).Select(static f => $"{f} is not listed in the manifest"));
        problems.AddRange(found.EmptyDirectories.Select(static d => $"{d}/ is an empty directory not used by the manifest"));
        return problems;
    }

    private static async Task<List<string>> VerifyCopyAsync(string directory, Bundle bundle)
    {
        var problems = await ContentProblemsAsync(directory, bundle);
        var manifestPath = Path.Combine(directory, Manifest);
        byte[]? raw = null;
        try
        {
            if (File.Exists(manifestPath)) raw = await File.ReadAllBytesAsync(manifestPath);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
        }
        if (raw is null || !raw.AsSpan().SequenceEqual(bundle.Raw))
            problems.Add($"{Manifest} is not a byte-for-byte copy of the export manifest");
        return problems;
    }

    private sealed record Destination(bool Exists, Dictionary<string, long> Files, byte[]? ManifestRaw, List<string> Unrecognized);

    /// <summary>
    /// What the destination holds. Files count as generated (replaceable) only when its current
    /// manifest lists them; anything else is unrecognized and blocks the installation.
    /// </summary>
    private static Destination InspectDestination(string target)
    {
        if (File.Exists(target))
            throw new InvalidOperationException($"The destination '{target}' is not a directory.");
        var info = new DirectoryInfo(target);
        if (!info.Exists) return new Destination(false, new Dictionary<string, long>(StringComparer.Ordinal), null, []);
        if (info.LinkTarget is not null)
            throw new InvalidOperationException($"The destination '{target}' is a symbolic link or junction; refusing to replace it.");

        var found = Walk(target);
        var owned = new HashSet<string>(StringComparer.Ordinal);
        var unrecognized = new List<string>(found.Unsafe);
        byte[]? manifestRaw = null;
        if (found.Files.ContainsKey(Manifest))
        {
            manifestRaw = File.ReadAllBytes(Path.Combine(target, Manifest));
            owned.Add(Manifest);
            try
            {
                using var doc = JsonDocument.Parse(manifestRaw);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Files", out var listed)
                    && listed.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in listed.EnumerateArray())
                    {
                        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("Path", out var path)
                            && path.ValueKind == JsonValueKind.String && PathProblem(path.GetString()) is null)
                            owned.Add(path.GetString()!);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                unrecognized.Add($"{Manifest} is not valid JSON, so no generated files can be identified");
            }
        }
        unrecognized.AddRange(found.Files.Keys.Where(f => !owned.Contains(f)).Select(static f => $"{f} is not listed in the installed manifest"));
        unrecognized.AddRange(found.EmptyDirectories.Select(static d => $"{d}/ is an empty directory"));
        return new Destination(true, found.Files, manifestRaw, unrecognized);
    }

    private static async Task<WebsiteInstallPlan> PlanAsync(Bundle bundle, Destination destination, string target)
    {
        List<string> added = [], replaced = [], removed = [];
        var unchanged = 0;
        foreach (var (file, expected) in bundle.Files.OrderBy(static f => f.Key, StringComparer.Ordinal))
        {
            if (!destination.Files.TryGetValue(file, out var length))
            {
                added.Add(file);
                continue;
            }
            var same = length == expected.Bytes && (await HashAsync(ToAbsolute(target, file))).Sha256 == expected.Sha256;
            if (same) unchanged++;
            else replaced.Add(file);
        }
        removed.AddRange(destination.Files.Keys.Where(f => f != Manifest && !bundle.Files.ContainsKey(f)).Order(StringComparer.Ordinal));
        var manifestChanged = destination.ManifestRaw is null || !destination.ManifestRaw.AsSpan().SequenceEqual(bundle.Raw);
        return new WebsiteInstallPlan(added, replaced, removed, unchanged, manifestChanged);
    }

    private static bool IsIoFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>Windows briefly locks directories that indexers, antivirus or file watchers have open.</summary>
    private static async Task MoveWithRetryAsync(InstallHooks hooks, string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                hooks.MoveDirectory(from, to);
                return;
            }
            catch (Exception ex) when (IsIoFailure(ex) && ex is not DirectoryNotFoundException && attempt < MoveRetries)
            {
                await Task.Delay(hooks.RetryDelay * Math.Pow(2, attempt));
            }
        }
    }

    /// <summary>Deletes a working directory this installation created. Links inside are removed, never followed.</summary>
    private static void RemoveQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
        }
    }
}
