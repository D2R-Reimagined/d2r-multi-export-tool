// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib;

namespace D2RMultiExport.TestSupport;

/// <summary>Shared optional/required real-data policy for the two test frameworks.</summary>
internal static class RealDataInputs
{
    internal static string? ModData => Environment.GetEnvironmentVariable("D2R_MOD_DATA");
    internal static string? CascData => Environment.GetEnvironmentVariable("D2R_CASC_DATA");
    internal static string? SharedExport => Environment.GetEnvironmentVariable("D2R_VALIDATION_EXPORT");
    internal static bool Required => Environment.GetEnvironmentVariable("D2R_REQUIRE_REAL_DATA") == "1";

    internal static string? SkipReason => GetSkipReason(ModData, CascData, Required);

    internal static string? GetSkipReason(string? modData, string? cascData, bool required)
    {
        return !required && (string.IsNullOrWhiteSpace(modData) || string.IsNullOrWhiteSpace(cascData))
            ? "Optional real-data validation skipped: set D2R_MOD_DATA and D2R_CASC_DATA."
            : null;
    }

    internal static SourceRoots RequireSources()
    {
        if (string.IsNullOrWhiteSpace(ModData) || string.IsNullOrWhiteSpace(CascData))
        {
            throw new InvalidOperationException(
                "Required real-data validation could not execute: set D2R_MOD_DATA and D2R_CASC_DATA.");
        }

        // Supplied but invalid layouts fail rather than becoming optional skips.
        var sources = SourceRoots.Create(ModData, CascData);
        if (sources.ModStringsDirectory is null || !Directory.Exists(sources.ModStringsDirectory))
        {
            throw new InvalidOperationException("Required real-data validation needs the mod's local/lng/strings directory.");
        }
        return sources;
    }
}
