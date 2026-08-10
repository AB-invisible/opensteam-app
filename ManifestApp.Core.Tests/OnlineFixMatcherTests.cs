using ManifestApp.Core;
using ManifestApp.Core.Models;
using Xunit;

namespace ManifestApp.Core.Tests;

public sealed class OnlineFixMatcherTests
{
    [Fact]
    public void FindMatch_matches_title_case_insensitively()
    {
        var fixes = new List<OnlineFixItem>
        {
            new() { Name = "other", Title = "Other Game" },
            new() { Name = "elden-ring", Title = "Elden Ring Online Fix" },
        };

        var match = OnlineFixMatcher.FindMatch(fixes, "Elden Ring");
        Assert.NotNull(match);
        Assert.Equal("elden-ring", match!.Name);
    }

    [Fact]
    public void FindMatch_returns_null_when_no_match()
    {
        var fixes = new List<OnlineFixItem>
        {
            new() { Name = "other", Title = "Other Game" },
        };

        Assert.Null(OnlineFixMatcher.FindMatch(fixes, "Missing Title"));
    }
}
