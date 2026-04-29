// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text.Json;

namespace D2RMultiExport.Client.Models;

public sealed class AppSettings
{
    public string ExcelPath { get; set; } = string.Empty;
    public string TranslationsPath { get; set; } = string.Empty;
    public string BaseStringsPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool PrettyPrintJson { get; set; } = true;
    public bool ContinueOnException { get; set; } = false;
    public bool EarlyStopSentinelEnabled { get; set; } = true;
    public bool CubeRecipeUseDescription { get; set; } = false;

    private static readonly string SettingsFile = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "D2RMultiExport", "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsFile)) return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
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
