// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Outcome of a destructive export (<see cref="DestructiveReplacement.RunAsync"/>).</summary>
/// <param name="Replaced">True when the output directory now holds only this run's export.</param>
/// <param name="RemovedEntries">Top-level entries of the previous contents that were removed.</param>
/// <param name="Message">Human-readable outcome, including the state the directory was left in.</param>
/// <param name="Warning">A non-fatal problem after a successful replacement, for example an undeletable previous copy.</param>
public sealed record DestructiveReplacementResult(bool Replaced, int RemovedEntries, string Message, string? Warning);

/// <summary>
/// The destructive export mode of the pipeline: the run is generated into a hidden working
/// directory inside the output directory, and only a run without errors replaces the
/// directory's previous contents. Replacement renames every previous top-level entry into a
/// hidden backup directory, moves the new entries up, and deletes the backup; a failed rename
/// moves everything back. Nothing outside the output directory is created, moved or deleted,
/// and links or junctions inside it are moved or removed, never followed.
/// <para>
/// The working names are <c>.d2r-export-new-*</c> and <c>.d2r-export-old-*</c>. A process
/// killed mid-replacement can leave them behind; the next destructive export removes them
/// together with the other previous contents.
/// </para>
/// </summary>
public static class DestructiveReplacement
{
    internal const string NewPrefix = ".d2r-export-new-";
    internal const string OldPrefix = ".d2r-export-old-";

    private const int MoveAttempts = 4;

    /// <summary>Folders whose contents must never be replaced wholesale, and folders that must never be contained by the output directory.</summary>
    private static readonly Environment.SpecialFolder[] SystemFolders =
    [
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
    ];

    /// <summary>
    /// Why replacing all contents of <paramref name="outputRoot"/> would be dangerous, or
    /// <c>null</c> when it is allowed. Refused: a drive or file-system root; a Git repository
    /// root; a user or system folder, or any folder containing one; any folder containing the
    /// exporter itself; and any folder that contains, or lies inside, one of
    /// <paramref name="protectedPaths"/> (the source and configuration directories).
    /// </summary>
    public static string? FindDanger(string outputRoot, IEnumerable<(string Label, string? Path)> protectedPaths)
    {
        string full;
        try
        {
            full = Normalize(outputRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"'{outputRoot}' is not a valid path: {ex.Message}";
        }
        if (File.Exists(full)) return $"'{full}' is a file, not a directory.";

        List<(string Label, string Path)> contained = [("the exporter's own program folder", Normalize(AppContext.BaseDirectory))];
        foreach (var folder in SystemFolders)
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length > 0) contained.Add(("a user or system folder", Normalize(path)));
        }
        if (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile)
            contained.Add(("a user or system folder", Normalize(Path.Combine(profile, "Downloads"))));
        List<(string Label, string Path)> sources = [];
        foreach (var (label, path) in protectedPaths)
        {
            if (!string.IsNullOrWhiteSpace(path)) sources.Add((label, Normalize(path)));
        }

        List<string> candidates = [full];
        if (RealPath.OfDirectory(full) is { } real && !SamePath(Normalize(real), full)) candidates.Add(Normalize(real));
        foreach (var path in candidates)
        {
            if (Path.GetPathRoot(path) is { Length: > 0 } root && SamePath(Normalize(root), path))
                return $"'{path}' is a drive or file-system root.";
            if (Path.Exists(Path.Combine(path, ".git")))
                return $"'{path}' is a Git repository root (it contains .git). Choose a folder inside the repository, for example its static{Path.DirectorySeparatorChar}data.";
            foreach (var (label, location) in contained.Concat(sources))
            {
                if (RealPath.IsUnder(location, path))
                    return $"'{path}' {(SamePath(location, path) ? "is" : "contains")} {label} ('{location}').";
            }
            foreach (var (label, location) in sources)
            {
                if (RealPath.IsUnder(path, location))
                    return $"'{path}' is inside {label} ('{location}').";
            }
        }
        return null;
    }

    /// <summary>Regression seam: renames one entry (<c>from</c>, <c>to</c>, <c>isDirectory</c>).</summary>
    internal delegate void MoveEntry(string from, string to, bool isDirectory);

    /// <summary>
    /// Runs <paramref name="generate"/> against a new hidden working directory inside
    /// <paramref name="outputRoot"/>. When it returns <c>true</c>, the previous contents of
    /// <paramref name="outputRoot"/> are replaced by exactly what it generated; otherwise, or
    /// when it throws, the previous contents are left as they were and the working directory is
    /// removed. A failed replacement is rolled back and reported as an <see cref="IOException"/>.
    /// </summary>
    internal static async Task<DestructiveReplacementResult> RunAsync(string outputRoot, Func<string, Task<bool>> generate, MoveEntry? move = null)
    {
        move ??= DefaultMove;
        var root = Normalize(outputRoot);
        Directory.CreateDirectory(root);
        var stamp = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var working = Path.Combine(root, NewPrefix + stamp);
        var old = Path.Combine(root, OldPrefix + stamp);
        Directory.CreateDirectory(working);
        var keepWorking = false;
        try
        {
            if (!await generate(working))
                return new DestructiveReplacementResult(false, 0,
                    $"The run recorded errors, so the existing contents of '{root}' were left unchanged.", null);

            var previous = new DirectoryInfo(root).EnumerateFileSystemInfos()
                .Where(entry => !string.Equals(entry.Name, Path.GetFileName(working), StringComparison.Ordinal))
                .Select(static entry => (entry.Name, IsDirectory: entry is DirectoryInfo))
                .ToList();
            var incoming = new DirectoryInfo(working).EnumerateFileSystemInfos()
                .Select(static entry => (entry.Name, IsDirectory: entry is DirectoryInfo))
                .ToList();
            Directory.CreateDirectory(old);

            var movedOut = new List<(string Name, bool IsDirectory)>();
            var movedIn = new List<(string Name, bool IsDirectory)>();
            try
            {
                foreach (var entry in previous)
                {
                    await MoveWithRetryAsync(move, Path.Combine(root, entry.Name), Path.Combine(old, entry.Name), entry.IsDirectory);
                    movedOut.Add(entry);
                }
                foreach (var entry in incoming)
                {
                    await MoveWithRetryAsync(move, Path.Combine(working, entry.Name), Path.Combine(root, entry.Name), entry.IsDirectory);
                    movedIn.Add(entry);
                }
            }
            catch (Exception ex) when (IsIoFailure(ex))
            {
                var rollback = new List<string>();
                foreach (var entry in Enumerable.Reverse(movedIn))
                    await TryMoveAsync(move, Path.Combine(root, entry.Name), Path.Combine(working, entry.Name), entry.IsDirectory, rollback);
                foreach (var entry in Enumerable.Reverse(movedOut))
                    await TryMoveAsync(move, Path.Combine(old, entry.Name), Path.Combine(root, entry.Name), entry.IsDirectory, rollback);
                if (rollback.Count > 0)
                {
                    keepWorking = true;
                    throw new IOException($"Replacing the contents of '{root}' failed ({ex.Message}) and could not be fully rolled back "
                                          + $"({string.Join("; ", rollback)}). The previous contents are in '{old}'; the new export is in '{working}'.", ex);
                }
                TryDelete(old, recursive: false);
                throw new IOException($"Replacing the contents of '{root}' failed ({ex.Message}); the previous contents were restored. "
                                      + "Close programs using the directory (for example a website dev server) and export again.", ex);
            }

            var warning = TryDelete(old, recursive: true)
                ? null
                : $"The previous contents of '{root}' were moved to '{old}' but could not be deleted; delete it by hand.";
            return new DestructiveReplacementResult(true, previous.Count,
                $"Replaced the contents of '{root}' with this export; {previous.Count} previous top-level entr{(previous.Count == 1 ? "y was" : "ies were")} removed.",
                warning);
        }
        finally
        {
            if (!keepWorking) TryDelete(working, recursive: true);
        }
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsIoFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static void DefaultMove(string from, string to, bool isDirectory)
    {
        // Renames the entry itself: a link or junction is moved, never its target.
        if (isDirectory) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Windows briefly locks entries that indexers, antivirus or file watchers have open.</summary>
    private static async Task MoveWithRetryAsync(MoveEntry move, string from, string to, bool isDirectory)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move(from, to, isDirectory);
                return;
            }
            catch (Exception ex) when (IsIoFailure(ex) && ex is not FileNotFoundException and not DirectoryNotFoundException && attempt < MoveAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    private static async Task TryMoveAsync(MoveEntry move, string from, string to, bool isDirectory, List<string> failures)
    {
        try
        {
            await MoveWithRetryAsync(move, from, to, isDirectory);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            failures.Add($"'{from}' -> '{to}': {ex.Message}");
        }
    }

    /// <summary>Deletes a working directory this run created. Links inside are removed, never followed.</summary>
    private static bool TryDelete(string directory, bool recursive)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive);
            return true;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return false;
        }
    }
}
