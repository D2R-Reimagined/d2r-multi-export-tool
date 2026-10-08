// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.Json;

namespace D2RMultiExport.Lib.Exporters;

/// <summary>Result of <see cref="PublishStager.StageAsync"/>.</summary>
/// <param name="Staged">Whether the staging directory was populated.</param>
/// <param name="Files">Files copied (manifest-relative paths), including <c>manifest.json</c>.</param>
/// <param name="Problems">Why staging was refused, or what failed verification.</param>
public sealed record PublishResult(bool Staged, IReadOnlyList<string> Files, IReadOnlyList<string> Problems);

/// <summary>
/// Builds a publishable copy of an export that contains <em>only</em> the files the last run
/// wrote and vouched for in <c>manifest.json</c>. The output directory deliberately keeps
/// files the tool did not write this run (skipped exporters, hand-placed files, other tools);
/// copying that directory wholesale would publish a mixed-generation bundle. Staging instead:
/// <list type="bullet">
/// <item><description>refuses a run whose manifest says <c>Succeeded: false</c>, and one with
/// <c>Complete: false</c> unless the caller explicitly allows an incomplete bundle;</description></item>
/// <item><description>copies exactly the manifest's <c>Files[]</c>, re-hashing each one and
/// refusing the whole stage if any file is missing or no longer matches its SHA-256 / size
/// (it was changed after the run);</description></item>
/// <item><description>writes into a new or empty staging directory outside the output root, and
/// never modifies or deletes anything in the output directory.</description></item>
/// </list>
/// </summary>
public static class PublishStager
{
    public static Task<PublishResult> StageAsync(string outputRoot, string stagingRoot, bool allowIncomplete)
        => StageAsync(outputRoot, stagingRoot, allowIncomplete, afterVerification: null);

    // Deterministic regression seam for a source replacement after preflight verification.
    internal static async Task<PublishResult> StageAsync(
        string outputRoot, string stagingRoot, bool allowIncomplete, Func<Task>? afterVerification)
    {
        var problems = new List<string>();
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        var staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));

        var physicalOutput = RealPath.OfDirectoryOrParent(output);
        var physicalStaging = RealPath.OfDirectoryOrParent(staging);
        if (RealPath.IsSupported && (physicalOutput is null || physicalStaging is null))
            return Refuse("Could not resolve the physical output/staging directories; nothing was staged.");
        output = physicalOutput ?? output;
        staging = physicalStaging ?? staging;

        if (RealPath.IsUnder(staging, output) || RealPath.IsUnder(output, staging))
            return Refuse($"The staging directory '{staging}' must not be the output directory '{output}' or nested with it.");
        if (Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any())
            return Refuse($"The staging directory '{staging}' is not empty. Choose a new or empty directory; nothing is deleted to make room.");

        var manifestPath = Path.Combine(output, ExportOutputs.ManifestFileName);
        if (!File.Exists(manifestPath))
            return Refuse($"'{manifestPath}' does not exist: the last run did not finish, so nothing in '{output}' is vouched for.");

        ManifestFile[] files;
        byte[] manifestBytes;
        try
        {
            manifestBytes = await File.ReadAllBytesAsync(manifestPath);
            using var doc = JsonDocument.Parse(manifestBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("SchemaVersion", out var schema) || schema.GetInt32() != ExportOutputs.ManifestSchemaVersion)
                return Refuse($"'{manifestPath}' has an unsupported SchemaVersion (expected {ExportOutputs.ManifestSchemaVersion}).");
            if (!root.GetProperty("Succeeded").GetBoolean())
                return Refuse("The run recorded errors (Succeeded: false). A failed run is never published.");
            if (!root.GetProperty("Complete").GetBoolean())
            {
                var incomplete = root.GetProperty("Steps").EnumerateArray()
                    .Where(static s => s.GetProperty("Status").GetString() != "Written" || s.TryGetProperty("NotWritten", out _))
                    .Select(static s => s.GetProperty("Producer").GetString() + " (" + s.GetProperty("Status").GetString() + ")")
                    .ToList();
                if (!allowIncomplete)
                    return Refuse($"The run is incomplete (Complete: false): {string.Join(", ", incomplete)}. "
                                  + "Re-run with every input available, or allow an incomplete bundle explicitly.");
                problems.Add($"Incomplete bundle allowed explicitly; missing from this run: {string.Join(", ", incomplete)}.");
            }
            files = [.. root.GetProperty("Files").EnumerateArray().Select(static f => new ManifestFile(
                f.GetProperty("Path").GetString()!, f.GetProperty("Bytes").GetInt64(), f.GetProperty("Sha256").GetString()!))];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Refuse($"'{manifestPath}' is not a valid manifest: {ex.Message}");
        }

        if (files.Any(static file => string.Equals(file.Path, ExportOutputs.ManifestFileName, StringComparison.OrdinalIgnoreCase)))
            return Refuse("The manifest must not list itself as a generated file.");

        // Preflight failures leave no partial copy. Each copied byte is verified again below.
        var outputs = new ExportOutputs(output);
        foreach (var file in files)
        {
            string source;
            try
            {
                source = outputs.GetFullPath(file.Path);
            }
            catch (InvalidOperationException ex)
            {
                problems.Add(ex.Message);
                continue;
            }
            if (!File.Exists(source))
            {
                problems.Add($"{file.Path}: listed in the manifest but missing on disk.");
                continue;
            }
            await using var stream = File.OpenRead(source);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            if (stream.Length != file.Bytes || !string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"{file.Path}: changed since the run (SHA-256/size no longer match the manifest).");
        }
        if (problems.Any(static p => !p.StartsWith("Incomplete bundle allowed", StringComparison.Ordinal)))
            return new PublishResult(false, [], problems);

        if (afterVerification is not null) await afterVerification();
        var staged = new ExportOutputs(staging) { Producer = "publish" };
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                var source = outputs.GetFullPath(file.Path);
                await staged.WriteAsync(file.Path, async destination =>
                {
                    await using var input = File.OpenRead(source);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long bytes = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer)) != 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        bytes += read;
                        await destination.WriteAsync(buffer.AsMemory(0, read));
                    }
                    var actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
                    if (bytes != file.Bytes || !string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"{file.Path}: changed while staging (SHA-256/size no longer match the captured manifest).");
                });
            }
            // A completed staging bundle is vouched for only by these captured bytes,
            // committed last after every staged payload passed its own hash/size check.
            await staged.WriteAsync(ExportOutputs.ManifestFileName, destination => destination.WriteAsync(manifestBytes).AsTask());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            problems.Add(ex.Message);
            return new PublishResult(false, [.. staged.Files.Select(static file => file.Path)], problems);
        }
        return new PublishResult(true, [.. files.Select(static f => f.Path), ExportOutputs.ManifestFileName], problems);

        PublishResult Refuse(string reason) => new(false, [], [reason]);
    }

    private sealed record ManifestFile(string Path, long Bytes, string Sha256);
}
