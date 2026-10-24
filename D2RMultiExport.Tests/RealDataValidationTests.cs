// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.TestSupport;

namespace D2RMultiExport.Tests;

/// <summary>Missing optional inputs skip; required or supplied inputs must reach validation.</summary>
public sealed class RealDataValidationTests
{
    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData(" ", "casc", false, true)]
    [InlineData("mod", null, false, true)]
    [InlineData(null, null, true, false)]
    [InlineData("mod", "", true, false)]
    [InlineData("mod", "casc", false, false)]
    [InlineData("invalid-but-supplied", "casc", true, false)]
    public void OnlyMissingOptionalInputsSkip(string? modData, string? cascData, bool required, bool skips)
    {
        Assert.Equal(skips, RealDataInputs.GetSkipReason(modData, cascData, required) is not null);
    }
}
