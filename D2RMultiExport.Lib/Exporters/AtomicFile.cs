// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>
/// Writes a file so that its final path only ever holds a complete document: the content
/// goes to a uniquely named sibling temp file, is flushed to disk, and then replaces the
/// destination in one <see cref="File.Move(string, string, bool)"/>. A serializer exception,
/// a full disk or a killed process therefore leaves the previous file (or no file) behind,
/// never a truncated one. The temp file is removed on failure; one abandoned by a killed
/// process is removed by the next write of the same file (<see cref="RemoveAbandonedTemps"/>).
/// </summary>
internal static class AtomicFile
{
    /// <summary>The temp name for <paramref name="fileName"/>: <c>.&lt;name&gt;.&lt;32 hex&gt;.tmp</c>.</summary>
    internal static string TempName(string fileName) => $".{fileName}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// Deletes the temp files a killed earlier run left for <paramref name="path"/>. Only names
    /// of the exact shape <see cref="TempName"/> produces for this file are candidates —
    /// <c>.&lt;file name&gt;.&lt;32 lowercase hex&gt;.tmp</c> in the same directory — so another
    /// tool's temp file, a temp for a different output, or any other name is never touched.
    /// A candidate another process still holds open is skipped. Returns the number removed.
    /// </summary>
    public static int RemoveAbandonedTemps(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is null || !Directory.Exists(directory)) return 0;
        var fileName = Path.GetFileName(path);
        var shape = new Regex($"^\\.{Regex.Escape(fileName)}\\.[0-9a-f]{{32}}\\.tmp$", RegexOptions.CultureInvariant);
        var removed = 0;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(directory, $".{fileName}.*.tmp", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        foreach (var candidate in candidates)
        {
            if (!shape.IsMatch(Path.GetFileName(candidate))) continue;
            if (new FileInfo(candidate).LinkTarget is not null) continue;
            try
            {
                // An exclusive open fails while a concurrent writer still has it open.
                using (new FileStream(candidate, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(candidate);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions? options) =>
        WriteAsync(path, stream => JsonSerializer.SerializeAsync(stream, value, options));

    public static Task WriteTextAsync(string path, string text) =>
        WriteAsync(path, async stream =>
        {
            var bytes = Utf8NoBom.GetBytes(text);
            await stream.WriteAsync(bytes);
        });

    /// <param name="path">Destination.</param>
    /// <param name="write">Produces the content.</param>
    /// <param name="verifyOpened">
    /// Called with the temp file's open stream before any content is written; throwing aborts
    /// the write (the temp file — which this call just created — is removed).
    /// </param>
    /// <param name="beforeCommit">Called immediately before the rename; throwing aborts it.</param>
    public static async Task WriteAsync(string path, Func<Stream, Task> write,
        Action<FileStream>? verifyOpened = null, Action? beforeCommit = null)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new IOException($"Cannot resolve the directory of '{path}'.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, TempName(Path.GetFileName(path)));
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 81920, FileOptions.Asynchronous))
            {
                verifyOpened?.Invoke(stream);
                await write(stream);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            beforeCommit?.Invoke();
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes <paramref name="path"/> if it exists. Returns true when a file was removed.</summary>
    public static bool DeleteIfExists(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // The original exception is the one worth reporting; a leftover dot-prefixed
            // temp file is harmless and is never referenced by the manifest.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
