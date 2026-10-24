// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D2RMultiExport.Client.Models;

public sealed class AppSettings
{
    /// <summary>The mod's <c>data</c> directory (see <c>D2RMultiExport.Lib.SourceRoots</c>).</summary>
    public string ModDataPath { get; set; } = string.Empty;

    /// <summary>The extracted vanilla CASC <c>data</c> directory (<c>data/</c> or <c>data/data/</c>).</summary>
    public string CascDataPath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Replace all existing contents of <see cref="OutputPath"/> after a run without errors (off by default).</summary>
    public bool DestructiveExport { get; set; } = false;

    /// <summary>Where a dry run keeps its error log; empty selects the default (<c>test-output/dryrun</c> of the repository).</summary>
    public string DryRunLogPath { get; set; } = string.Empty;

    public bool PrettyPrintJson { get; set; } = true;
    public bool ContinueOnException { get; set; } = false;
    public bool EarlyStopSentinelEnabled { get; set; } = true;
    public bool CubeRecipeUseDescription { get; set; } = false;

    // Pre-SourceRoots settings, read once to migrate and never written again.
    [JsonPropertyName("ExcelPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyExcelPath { get; set; }

    [JsonPropertyName("BaseAssetsPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyBaseAssetsPath { get; set; }

    [JsonPropertyName("TranslationsPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyTranslationsPath { get; set; }

    [JsonPropertyName("BaseStringsPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? LegacyBaseStringsPath { get; set; }

    private static readonly string DefaultSettingsFile = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "D2RMultiExport", "settings.json");

    /// <summary>Overridable for tests; defaults to <c>%APPDATA%/D2RMultiExport/settings.json</c>.</summary>
    public static string SettingsFile { get; set; } = DefaultSettingsFile;

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsFile)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            settings.MigrateLegacyPaths();
            return settings;
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>
    /// Settings written before the two-root configuration stored the excel folder and the
    /// vanilla data folder. The mod root is the excel folder minus its fixed
    /// <c>global/excel</c> suffix — a string operation on a known layout, not a directory
    /// walk. The CASC asset root carries over as-is (the resolver recognises its layouts).
    /// If only base strings were saved, remove their fixed <c>local/lng/strings</c> suffix.
    /// Modern roots take precedence, then the legacy asset root, then the strings fallback.
    /// </summary>
    internal void MigrateLegacyPaths()
    {
        if (string.IsNullOrWhiteSpace(ModDataPath) && !string.IsNullOrWhiteSpace(LegacyExcelPath))
        {
            var excel = LegacyExcelPath.TrimEnd('\\', '/');
            foreach (var suffix in new[] { @"\global\excel", "/global/excel" })
            {
                if (excel.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    ModDataPath = excel[..^suffix.Length];
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(CascDataPath) && !string.IsNullOrWhiteSpace(LegacyBaseAssetsPath))
            CascDataPath = LegacyBaseAssetsPath;
        if (string.IsNullOrWhiteSpace(CascDataPath) && !string.IsNullOrWhiteSpace(LegacyBaseStringsPath))
        {
            var strings = LegacyBaseStringsPath.TrimEnd('\\', '/');
            foreach (var suffix in new[] { @"\local\lng\strings", "/local/lng/strings" })
            {
                if (strings.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    CascDataPath = strings[..^suffix.Length];
                    break;
                }
            }
        }
        LegacyExcelPath = null;
        LegacyBaseAssetsPath = null;
        LegacyTranslationsPath = null;
        LegacyBaseStringsPath = null;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch { /* Ignore save errors */ }
    }
}
