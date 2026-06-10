using D2RMultiExport.Lib.Config;
using D2RMultiExport.Lib.Import;
using D2RMultiExport.Lib.Models;
using D2RMultiExport.Lib.Translation;

namespace D2RMultiExport.Tests;

public sealed class RequirementHelperTests
{
    [Fact]
    public void ComputeAdjustedRequiredLevel_counts_property_alias_that_maps_to_item_levelreq()
    {
        var data = new GameData
        {
            ExportConfig = new ExportConfig(),
            StatOverrideConfig = new StatOverrideConfig(),
            Translations = new TranslationService()
        };
        data.Properties["levelreq"] = new PropertyEntry
        {
            Code = "levelreq",
            Stat1 = "item_levelreq"
        };

        var properties = new[]
        {
            new CubePropertyExport
            {
                PropertyCode = "levelreq",
                Min = 26,
                Max = 26
            }
        };

        var requiredLevel = RequirementHelper.ComputeAdjustedRequiredLevel(
            data,
            itemType: "Runeword",
            itemName: "Law",
            baseReqLevel: 0,
            properties: properties);

        Assert.Equal(26, requiredLevel);
    }
}
