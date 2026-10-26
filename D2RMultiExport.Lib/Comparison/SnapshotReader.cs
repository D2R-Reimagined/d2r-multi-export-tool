// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text.Json;
using D2RMultiExport.Lib.Exporters;

namespace D2RMultiExport.Lib.Comparison;

/// <summary>Validates one manifest-backed snapshot and reads verified current-run files.</summary>
internal sealed class SnapshotReader
{
    private const string RequiredProducer = "keyed-core";
    private readonly string _root;
    private readonly List<ManifestFile> _files = [];
    private readonly List<ManifestStep> _steps = [];
    private readonly ExportOutputs _pathValidator;
    private bool _manifestValid;

    private SnapshotReader(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _pathValidator = new ExportOutputs(_root);
        Info = new SnapshotInfo { Path = _root };
    }

    public SnapshotInfo Info { get; private set; }

    public static async Task<SnapshotReader> LoadAsync(string root)
    {
        var reader = new SnapshotReader(root);
        await reader.LoadManifestAsync();
        return reader;
    }

    public async Task<SnapshotFileResult> ReadFileAsync(string relativePath, string requiredProducer = RequiredProducer)
    {
        var errors = new List<ComparisonIssue>();
        var warnings = new List<ComparisonIssue>();

        void AddFileError(string code, string message) =>
            errors.Add(new ComparisonIssue { Code = code, Message = message });

        void AddFileWarning(string code, string message) =>
            warnings.Add(new ComparisonIssue { Code = code, Message = message });

        if (!_manifestValid || Info.Succeeded != true) return new SnapshotFileResult(null, errors, warnings);

        var matchingSteps = _steps.Where(step => step.Producer == requiredProducer).ToList();
        if (matchingSteps.Count != 1)
        {
            AddFileError(matchingSteps.Count == 0 ? "missing-export-step" : "duplicate-export-step",
                matchingSteps.Count == 0
                    ? $"Manifest has no '{requiredProducer}' export step for '{relativePath}'."
                    : $"Manifest has more than one '{requiredProducer}' export step for '{relativePath}'.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        var step = matchingSteps[0];

        if (!string.Equals(step.Status, "Written", StringComparison.Ordinal))
        {
            var detail = step.NotWritten.Contains(relativePath, StringComparer.Ordinal)
                ? "The file was declared but not written during this run; any on-disk copy is stale."
                : $"The exporter step status is '{step.Status}', so this file is not trusted as a current-run output.";
            AddFileError("export-step-not-written", $"'{relativePath}' cannot be used. {detail}");
            return new SnapshotFileResult(null, errors, warnings);
        }

        var outputListed = step.Outputs.Contains(relativePath, StringComparer.Ordinal);
        var notWritten = step.NotWritten.Contains(relativePath, StringComparer.Ordinal);
        if (outputListed && notWritten)
        {
            AddFileError("file-marked-not-written",
                $"'{relativePath}' appears in both the step outputs and NotWritten lists.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        if (notWritten)
        {
            AddFileError("export-file-not-written",
                $"'{relativePath}' was not written during this run; any on-disk copy is stale.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        if (!outputListed)
        {
            AddFileError("file-not-in-export-step",
                $"'{relativePath}' is not listed as an output of the '{requiredProducer}' step.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        var matchingFiles = _files.Where(file => string.Equals(file.Path, relativePath, StringComparison.Ordinal))
            .ToList();
        if (matchingFiles.Count != 1)
        {
            AddFileError(matchingFiles.Count == 0 ? "file-not-in-manifest" : "duplicate-file-manifest-entry",
                matchingFiles.Count == 0
                    ? $"'{relativePath}' is not in manifest Files[]; an on-disk copy is not proof that this run produced it."
                    : $"Manifest Files[] contains more than one entry for '{relativePath}'.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        var file = matchingFiles[0];
        if (!string.Equals(file.Producer, step.Producer, StringComparison.Ordinal))
        {
            AddFileError("file-producer-mismatch",
                $"Manifest lists '{relativePath}' under producer '{file.Producer}', not '{step.Producer}'.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        string path;
        try
        {
            path = _pathValidator.GetFullPath(relativePath);
        }
        catch (InvalidOperationException ex)
        {
            AddFileError("unsafe-snapshot-path", $"Cannot safely read '{relativePath}': {ex.Message}");
            return new SnapshotFileResult(null, errors, warnings);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddFileError("snapshot-file-unreadable", $"Cannot read current-run file '{relativePath}': {ex.Message}");
            return new SnapshotFileResult(null, errors, warnings);
        }

        if (file.Bytes is { } expectedBytes && bytes.LongLength != expectedBytes)
        {
            AddFileError("snapshot-file-size-mismatch",
                $"'{relativePath}' has {bytes.LongLength} bytes but the manifest records {expectedBytes}.");
            return new SnapshotFileResult(null, errors, warnings);
        }

        if (file.Bytes is null)
            AddFileWarning("file-size-unavailable",
                $"Manifest has no byte length for '{relativePath}'; only available integrity metadata can be checked.");

        string? verifiedHash = null;
        if (file.Sha256 is { Length: > 0 } expectedHash)
        {
            if (expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            {
                AddFileError("invalid-file-hash", $"Manifest has an invalid SHA-256 value for '{relativePath}'.");
                return new SnapshotFileResult(null, errors, warnings);
            }

            verifiedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(verifiedHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                AddFileError("snapshot-file-hash-mismatch",
                    $"SHA-256 mismatch for '{relativePath}'; the snapshot payload may have changed.");
                return new SnapshotFileResult(null, errors, warnings);
            }
        }
        else
        {
            AddFileWarning("file-hash-unavailable",
                $"Manifest has no SHA-256 for '{relativePath}'; the file cannot be verified against a hash.");
        }

        return new SnapshotFileResult(new SnapshotPayload(bytes, verifiedHash), errors, warnings);
    }

    private async Task LoadManifestAsync()
    {
        if (!Directory.Exists(_root))
        {
            AddError("snapshot-directory-missing", $"Snapshot directory '{_root}' does not exist.");
            return;
        }

        string manifestPath;
        try
        {
            manifestPath = _pathValidator.GetFullPath(ExportOutputs.ManifestFileName);
        }
        catch (InvalidOperationException ex)
        {
            AddError("unsafe-manifest-path", $"Cannot safely read manifest.json: {ex.Message}");
            return;
        }

        byte[] manifestBytes;
        try
        {
            manifestBytes = await File.ReadAllBytesAsync(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddError("manifest-unreadable", $"Cannot read '{manifestPath}': {ex.Message}");
            return;
        }

        Info = SetManifestHash(Info, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
        using var document = ParseManifest(manifestBytes);
        if (document is null) return;
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object)
        {
            AddError("invalid-manifest", "manifest.json root must be an object.");
            return;
        }

        if (TryReadInt(manifest, "SchemaVersion", out var schemaVersion))
        {
            Info = SetSchemaVersion(Info, schemaVersion);
            if (schemaVersion != ExportOutputs.ManifestSchemaVersion)
                AddError("unsupported-manifest-schema",
                    $"Manifest schema {schemaVersion} is unsupported; expected {ExportOutputs.ManifestSchemaVersion}.");
        }
        else
            AddError("missing-manifest-schema", "manifest.json has no integer SchemaVersion.");

        if (TryReadBool(manifest, "Succeeded", out var succeeded))
        {
            Info = SetSucceeded(Info, succeeded);
            if (!succeeded)
                AddError("snapshot-failed",
                    "The export manifest records Succeeded=false; its data is not safe to compare.");
        }
        else
            AddError("missing-manifest-succeeded", "manifest.json has no boolean Succeeded value.");

        if (TryReadBool(manifest, "Complete", out var complete))
        {
            Info = SetComplete(Info, complete);
            if (!complete)
                Info.Warnings.Add(new ComparisonIssue
                {
                    Code = "snapshot-incomplete",
                    Message =
                        "The export manifest records Complete=false. Only category files explicitly listed as written by this run can be compared."
                });
        }
        else
            AddError("missing-manifest-complete", "manifest.json has no boolean Complete value.");

        ReadSteps(manifest);
        ReadFiles(manifest);
        _manifestValid = Info.ManifestSchemaVersion == ExportOutputs.ManifestSchemaVersion
                         && Info.Succeeded.HasValue && Info.Complete.HasValue && Info.Errors.Count == 0;
    }

    private JsonDocument? ParseManifest(byte[] bytes)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            AddError("invalid-manifest-json", $"manifest.json is invalid JSON: {ex.Message}");
            return null;
        }
    }

    private void ReadSteps(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("Steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
        {
            AddError("missing-manifest-steps", "manifest.json has no Steps array.");
            return;
        }

        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object
                || !TryReadString(step, "Producer", out var producer)
                || !TryReadString(step, "Status", out var status)
                || !TryReadStringArray(step, "Outputs", out var outputs))
            {
                AddError("invalid-manifest-step",
                    "manifest Steps[] contains an entry without valid Producer, Status, or Outputs values.");
                continue;
            }

            var notWritten = TryReadStringArray(step, "NotWritten", out var values) ? values : [];
            _steps.Add(new ManifestStep(producer, status, outputs, notWritten));
        }
    }

    private void ReadFiles(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            AddError("missing-manifest-files", "manifest.json has no Files array.");
            return;
        }

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || !TryReadString(file, "Path", out var path))
            {
                AddError("invalid-manifest-file", "manifest Files[] contains an entry without a string Path.");
                continue;
            }

            long? bytes = TryReadLong(file, "Bytes", out var byteCount) ? byteCount : null;
            var sha256 = TryReadString(file, "Sha256", out var hash) ? hash : null;
            var producer = TryReadString(file, "Producer", out var producerName) ? producerName : "";
            _files.Add(new ManifestFile(path, bytes, sha256, producer));
        }
    }

    private void AddError(string code, string message) =>
        Info.Errors.Add(new ComparisonIssue { Code = code, Message = message });

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryReadStringArray(JsonElement element, string name, out List<string> values)
    {
        values = [];
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            values.Add(item.GetString() ?? "");
        }

        return true;
    }

    private static bool TryReadInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryReadLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static bool TryReadBool(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = property.GetBoolean();
        return true;
    }

    private static SnapshotInfo SetManifestHash(SnapshotInfo info, string hash) => new()
    {
        Path = info.Path,
        ManifestSha256 = hash,
        ManifestSchemaVersion = info.ManifestSchemaVersion,
        Succeeded = info.Succeeded,
        Complete = info.Complete,
        Errors = info.Errors,
        Warnings = info.Warnings
    };

    private static SnapshotInfo SetSchemaVersion(SnapshotInfo info, int value) => new()
    {
        Path = info.Path,
        ManifestSha256 = info.ManifestSha256,
        ManifestSchemaVersion = value,
        Succeeded = info.Succeeded,
        Complete = info.Complete,
        Errors = info.Errors,
        Warnings = info.Warnings
    };

    private static SnapshotInfo SetSucceeded(SnapshotInfo info, bool value) => new()
    {
        Path = info.Path,
        ManifestSha256 = info.ManifestSha256,
        ManifestSchemaVersion = info.ManifestSchemaVersion,
        Succeeded = value,
        Complete = info.Complete,
        Errors = info.Errors,
        Warnings = info.Warnings
    };

    private static SnapshotInfo SetComplete(SnapshotInfo info, bool value) => new()
    {
        Path = info.Path,
        ManifestSha256 = info.ManifestSha256,
        ManifestSchemaVersion = info.ManifestSchemaVersion,
        Succeeded = info.Succeeded,
        Complete = value,
        Errors = info.Errors,
        Warnings = info.Warnings
    };

    private sealed record ManifestFile(string Path, long? Bytes, string? Sha256, string Producer);

    private sealed record ManifestStep(string Producer, string Status, List<string> Outputs, List<string> NotWritten);
}

internal sealed record SnapshotPayload(byte[] Bytes, string? Sha256);

internal sealed record SnapshotFileResult(
    SnapshotPayload? Payload,
    List<ComparisonIssue> Errors,
    List<ComparisonIssue> Warnings);
