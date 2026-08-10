using ManifestApp.Core;
using Xunit;

namespace ManifestApp.Core.Tests;

public sealed class OnlineFixDisplayHelperTests
{
    [Theory]
    [InlineData("20XX по сети - 20XX Fix Repair Steam Generic", null, "20XX")]
    [InlineData("A Way Out по сети - AWO Fix Repair Steam", null, "A Way Out")]
    [InlineData("Palworld Fix Repair Steam Generic", "Palworld_Fix_Repair_Steam_Generic.rar", "Palworld")]
    public void ParseDisplayTitle_strips_onlinefix_boilerplate(string raw, string? fileName, string expected)
    {
        Assert.Equal(expected, OnlineFixDisplayHelper.ParseDisplayTitle(raw, fileName));
    }
}
