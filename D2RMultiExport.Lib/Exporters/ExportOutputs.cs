// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>One file written during the current run, relative to the export root.</summary>
/// <param name="Path">Root-relative path with <c>/</c> separators (e.g. <c>keyed/uniques.json</c>).</param>
/// <param name="Producer">The pipeline step that wrote it (e.g. <c>drop-calculator.json</c>).</param>
public sealed record ExportedFile(string Path, string Producer);

/// <summary>Outcome of one pipeline export step, as recorded in <c>manifest.json</c>.</summary>
public enum ExportStepStatus
{
    /// <summary>The step finished and wrote at least one output during this run.</summary>
    Written,

    /// <summary>
    /// The step finished without attempting any output (an optional input was absent).
    /// Existing files at its output paths were left untouched and are not vouched for.
    /// </summary>
    Skipped,

    /// <summary>
    /// The step threw. Only the outputs it attempted to write during this run were
    /// removed; outputs it never attempted were left untouched and are not vouched for.
    /// </summary>
    Failed
}

/// <summary>One pipeline export step and what happened to it.</summary>
/// <param name="Producer">Step name, matching <see cref="ExportedFile.Producer"/>.</param>
/// <param name="Status">Outcome of the step.</param>
/// <param name="Outputs">Files this step wrote during this run (all listed in the manifest's <c>Files</c>).</param>
/// <param name="NotWritten">Declared outputs this run did not produce. Any copy on disk is from an earlier run and was not touched.</param>
/// <param name="Removed">Files deleted because the step failed after attempting to write them.</param>
/// <param name="Message">Reason for a skip or failure.</param>
public sealed record ExportStepResult(
    string Producer,
    ExportStepStatus Status,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> NotWritten,
    IReadOnlyList<string> Removed,
    string? Message);

/// <summary>
/// The single gateway every exporter writes through. It roots every path at the export
/// directory (refusing anything that would escape it, including through a symbolic link
/// or junction below the root), writes atomically via <see cref="AtomicFile"/>, and
/// records both every write <em>attempt</em> and every completed write, stamped with the
/// producing step.
/// <para>
/// Ownership is explicit: the only files this class will ever delete are ones a step
/// attempted to write during <em>this</em> run (<see cref="InvalidateAttempted"/>) and the
/// run's own <see cref="ManifestFileName"/>. It never scans a directory, so manually placed
/// files, files of other tools and outputs of skipped steps are never touched.
/// </para>
/// </summary>
public sealed class ExportOutputs
{
    /// <summary>Name of the run manifest written at the export root.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>Version of the <c>manifest.json</c> contract; see <c>docs/WEBSITE-INTEGRATION.md</c> §9.</summary>
    public const int ManifestSchemaVersion = 2;

    private readonly Dictionary<string, ExportedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ExportOutputs(string root)
    {
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
    }

    /// <summary>Absolute export root (the <c>--out</c> directory).</summary>
    public string Root { get; }

    /// <summary>The pipeline step currently writing; stamped onto every recorded file.</summary>
    public string Producer { get; internal set; } = "";

    /// <summary>Every file written so far in this run, in write order.</summary>
    public IReadOnlyList<ExportedFile> Files
    {
        get
        {
            lock (_gate) return [.. _files.Values];
        }
    }

    /// <summary>True when <paramref name="relativePath"/> was written during this run.</summary>
    public bool WasWritten(string relativePath)
    {
        var key = Normalize(relativePath);
        lock (_gate) return _files.ContainsKey(key);
    }

    /// <summary>Every path <paramref name="producer"/> attempted to write during this run, successful or not.</summary>
    public IReadOnlyList<string> AttemptedBy(string producer)
    {
        lock (_gate)
            return [.. _attempts.Where(a => a.Value == producer).Select(static a => a.Key)];
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <see cref="Root"/>. Throws when the
    /// path is rooted, when it resolves outside the export directory, or when any existing
    /// directory or file between the root and the target is a symbolic link or junction —
    /// a crafted asset name or a planted link must never redirect a write or delete
    /// elsewhere. The root itself may be a link: it is the location the user chose.
    /// </summary>
    public string GetFullPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || System.IO.Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Refusing output path '{relativePath}': it must be a non-empty path relative to the export directory '{Root}'.");

        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relativePath));
        var rootWithSeparator = Root.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? Root
            : Root + System.IO.Path.DirectorySeparatorChar;
        // Ordinal on purpose: Combine keeps the root's spelling, so a legitimate path always
        // matches exactly, while a case-insensitive match would let "../OUT/x" escape on a
        // case-sensitive file system.
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing output path '{relativePath}': it resolves outside the export directory '{Root}'.");

        var current = Root;
        foreach (var segment in full[rootWithSeparator.Length..].Split(System.IO.Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
                throw new InvalidOperationException($"Refusing output path '{relativePath}': '{current}' is a symbolic link or junction.");
        }
        return full;
    }

    public Task WriteJsonAsync<T>(string relativePath, T value, JsonSerializerOptions? options) =>
        WriteAsync(relativePath, stream => JsonSerializer.SerializeAsync(stream, value, options));

    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public Task WriteTextAsync(string relativePath, string text) =>
        WriteAsync(relativePath, async stream => await stream.WriteAsync(Utf8NoBom.GetBytes(text)));

    public async Task WriteAsync(string relativePath, Func<Stream, Task> write)
    {
        var key = Normalize(relativePath);
        // Recorded before validation so that a refused or failed write still counts as an
        // attempt the step owns; a later invalidation re-validates before deleting anything.
        lock (_gate) _attempts[key] = Producer;
        var full = GetFullPath(relativePath);
        // Test seam: lets a test plant a link in the window between validation and creation.
        AfterValidation?.Invoke(full);
        var abandoned = AtomicFile.RemoveAbandonedTemps(full);
        if (abandoned > 0) Interlocked.Add(ref _abandonedTempsRemoved, abandoned);
        await AtomicFile.WriteAsync(full, write,
            verifyOpened: stream => VerifyInsideRoot(stream, relativePath),
            // Re-walk the components right before the rename: a link planted while the
            // content was being written is refused here instead of being followed.
            beforeCommit: () => GetFullPath(relativePath));
        lock (_gate) _files[key] = new ExportedFile(key, Producer);
    }

    private int _abandonedTempsRemoved;
    private string? _realRoot;

    /// <summary>Test seam invoked between path validation and temp-file creation.</summary>
    internal Action<string>? AfterValidation { get; set; }

    /// <summary>Temp files of killed earlier runs removed by this run (see <see cref="AtomicFile.RemoveAbandonedTemps"/>).</summary>
    public int AbandonedTempsRemoved => Volatile.Read(ref _abandonedTempsRemoved);

    /// <summary>
    /// Second containment check, made against the file system rather than a path string: the
    /// temp file has just been created, so its handle names where the bytes will really go.
    /// If a symbolic link or junction was swapped into the path after <see cref="GetFullPath"/>
    /// validated it, the real location is outside the real export root and the write is
    /// refused before any content is written. Windows and Linux only (see <see cref="RealPath"/>).
    /// </summary>
    private void VerifyInsideRoot(FileStream stream, string relativePath)
    {
        if (!RealPath.IsSupported) return;
        _realRoot ??= RealPath.OfDirectory(Root);
        var real = RealPath.OfHandle(stream.SafeFileHandle);
        if (_realRoot is null || real is null) return;
        if (!RealPath.IsUnder(real, _realRoot))
            throw new InvalidOperationException(
                $"Refusing output path '{relativePath}': the file was created at '{real}', outside the export directory '{_realRoot}' (a link was changed during the write).");
    }

    /// <summary>
    /// Invalidates the outputs of a failed step: every path <paramref name="producer"/>
    /// attempted to write during this run is forgotten and deleted from disk, because the
    /// step's set of outputs is incomplete and an attempted path may still hold a previous
    /// run's copy. Paths the step never attempted are not touched. Returns the paths that
    /// were actually deleted.
    /// </summary>
    public IReadOnlyList<string> InvalidateAttempted(string producer) =>
        InvalidateAttempted(producer, static _ => true);

    /// <summary>As <see cref="InvalidateAttempted(string)"/>, restricted to the attempted paths matching <paramref name="filter"/>.</summary>
    public IReadOnlyList<string> InvalidateAttempted(string producer, Func<string, bool> filter)
    {
        List<string> removed = [];
        foreach (var path in AttemptedBy(producer).Where(filter))
        {
            lock (_gate)
            {
                _files.Remove(path);
                _attempts.Remove(path);
            }
            string full;
            try
            {
                full = GetFullPath(path);
            }
            catch (InvalidOperationException)
            {
                // The attempt was refused by containment in the first place, so nothing was
                // written there and nothing outside the export directory may be deleted.
                continue;
            }
            if (AtomicFile.DeleteIfExists(full))
                removed.Add(path);
        }
        return removed;
    }

    /// <summary>
    /// Deletes the previous run's <see cref="ManifestFileName"/>. The manifest is owned
    /// exclusively by this tool and describes exactly one run, so it is removed up front: a
    /// run that dies before the end leaves no manifest rather than one vouching for files
    /// this run may have replaced.
    /// </summary>
    internal bool RemovePreviousManifest() => AtomicFile.DeleteIfExists(GetFullPath(ManifestFileName));

    /// <summary>
    /// Writes <c>manifest.json</c>: every file this run produced with its size and SHA-256,
    /// plus the status of each export step. <paramref name="succeeded"/> is false whenever
    /// the run recorded an error; <c>Complete</c> is additionally false when any step was
    /// skipped or left a declared output unwritten, so a consumer can tell a full bundle
    /// from one that relies on files from an earlier run.
    /// </summary>
    internal async Task WriteManifestAsync(bool succeeded, IReadOnlyList<ExportStepResult> steps, bool prettyPrint)
    {
        var files = new List<object>();
        foreach (var file in Files.OrderBy(static f => f.Path, StringComparer.Ordinal))
        {
            var fullPath = GetFullPath(file.Path);
            await using var stream = File.OpenRead(fullPath);
            var hash = await SHA256.HashDataAsync(stream);
            files.Add(new { file.Path, Bytes = stream.Length, Sha256 = Convert.ToHexStringLower(hash), file.Producer });
        }

        var complete = succeeded && steps.All(static step =>
            step.Status == ExportStepStatus.Written && step.NotWritten.Count == 0);
        var manifest = new
        {
            SchemaVersion = ManifestSchemaVersion,
            Succeeded = succeeded,
            Complete = complete,
            // Temp files a killed earlier run left behind for outputs this run rewrote.
            AbandonedTempFilesRemoved = AbandonedTempsRemoved,
            Steps = steps.Select(static step => new
            {
                step.Producer,
                Status = step.Status.ToString(),
                step.Outputs,
                NotWritten = step.NotWritten.Count > 0 ? step.NotWritten : null,
                Removed = step.Removed.Count > 0 ? step.Removed : null,
                step.Message
            }),
            Files = files
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var manifestPath = GetFullPath(ManifestFileName);
        AtomicFile.RemoveAbandonedTemps(manifestPath);
        await AtomicFile.WriteAsync(manifestPath, stream => JsonSerializer.SerializeAsync(stream, manifest, options),
            verifyOpened: stream => VerifyInsideRoot(stream, ManifestFileName),
            beforeCommit: () => GetFullPath(ManifestFileName));
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');
}
