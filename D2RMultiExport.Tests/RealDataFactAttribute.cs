// SPDX-License-Identifier: GPL-3.0-or-later
using D2RMultiExport.TestSupport;

namespace D2RMultiExport.Tests;

/// <summary>Reports optional missing inputs as an xUnit v2 skip; required mode executes and fails.</summary>
public sealed class RealDataFactAttribute : FactAttribute
{
    public RealDataFactAttribute()
    {
        Skip = RealDataInputs.SkipReason;
    }
}
