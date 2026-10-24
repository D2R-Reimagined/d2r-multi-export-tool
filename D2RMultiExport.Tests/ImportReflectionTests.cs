// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.Lib.Import;

namespace D2RMultiExport.Tests;

/// <summary>Absent cells, malformed cells and invalid column references are reported differently.</summary>
public sealed class ImportReflectionTests
{
    private sealed class Row
    {
        public string? Mod1Code { get; init; }
        public string? Mod1Min { get; init; }
        public int? Mod1Max { get; init; }
        public bool Flag { get; init; }
    }

    [Fact]
    public void EmptyCellsReadAsNullWithoutDiagnostics()
    {
        var warnings = new List<string>();
        var row = new Row { Mod1Min = "  " };

        Assert.Null(ImportReflection.GetString(row, nameof(Row.Mod1Code)));
        Assert.Null(ImportReflection.GetInt(row, nameof(Row.Mod1Min), warnings.Add));
        Assert.Null(ImportReflection.GetInt(row, nameof(Row.Mod1Max), warnings.Add));
        Assert.Empty(warnings);
    }

    [Fact]
    public void TypedAndStringIntegersAreRead()
    {
        var row = new Row { Mod1Min = "-5", Mod1Max = 7 };
        Assert.Equal(-5, ImportReflection.GetInt(row, nameof(Row.Mod1Min)));
        Assert.Equal(7, ImportReflection.GetInt(row, nameof(Row.Mod1Max)));
    }

    [Fact]
    public void MalformedIntegerIsReportedAndIgnored()
    {
        var warnings = new List<string>();
        Assert.Null(ImportReflection.GetInt(new Row { Mod1Min = "ten" }, nameof(Row.Mod1Min), warnings.Add));
        var warning = Assert.Single(warnings);
        Assert.Contains("Mod1Min", warning);
        Assert.Contains("ten", warning);
    }

    [Fact]
    public void MissingColumnThrowsWithTypePropertyAndCaller()
    {
        var ex = Assert.Throws<ImportColumnException>(() => ImportReflection.GetString(new Row(), "Mod9Code"));
        Assert.Contains(nameof(Row), ex.Message);
        Assert.Contains("Mod9Code", ex.Message);
        Assert.Contains(nameof(MissingColumnThrowsWithTypePropertyAndCaller), ex.Message);
    }

    [Fact]
    public void WrongColumnTypeThrows()
    {
        Assert.Throws<ImportColumnException>(() => ImportReflection.GetString(new Row(), nameof(Row.Mod1Max)));
        Assert.Throws<ImportColumnException>(() => ImportReflection.GetInt(new Row(), nameof(Row.Flag)));
    }
}
