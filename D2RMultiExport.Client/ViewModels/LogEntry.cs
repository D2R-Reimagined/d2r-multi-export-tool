// SPDX-License-Identifier: GPL-3.0-or-later
using System;

namespace D2RMultiExport.Client.ViewModels;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

/// <summary>One line of the run log, classified so the viewer can filter and colour it.</summary>
public sealed record LogEntry(DateTime Time, LogLevel Level, string Text)
{
    public bool IsWarning => Level == LogLevel.Warning;

    public bool IsError => Level == LogLevel.Error;

    /// <summary>
    /// Classifies a pipeline line by the markers the pipeline and the import report use
    /// (<c>[ERROR]</c>, <c>[FATAL]</c>, <c>[WARN]</c>).
    /// </summary>
    public static LogEntry From(string text) => new(DateTime.Now, Classify(text), text);

    public static LogLevel Classify(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("[FATAL]", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Error;
        if (trimmed.StartsWith("[WARN]", StringComparison.OrdinalIgnoreCase))
            return LogLevel.Warning;
        return LogLevel.Info;
    }

    public override string ToString() => Text;
}
