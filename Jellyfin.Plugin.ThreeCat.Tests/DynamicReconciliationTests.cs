using Jellyfin.Plugin.ThreeCat.Dynamic;
using Jellyfin.Plugin.ThreeCat.ThreeCat;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class DynamicReconciliationTests
{
    [Fact]
    public void MissingItemsRequireThreeCompleteObservations()
    {
        var a = DynamicReconciliation.Merge(new[] { "a", "b" }, new[] { "a" }, null, x => x);
        Assert.Contains("b", a.Items);
        var b = DynamicReconciliation.Merge(a.Items, new[] { "a" }, a.Missing, x => x);
        Assert.Contains("b", b.Items);
        var c = DynamicReconciliation.Merge(b.Items, new[] { "a" }, b.Missing, x => x);
        Assert.DoesNotContain("b", c.Items);
        var returned = DynamicReconciliation.Merge(b.Items, new[] { "a", "b" }, b.Missing, x => x);
        Assert.Empty(returned.Missing);
    }

    [Fact]
    public void ExplicitTargetIsRequiredForSx3Episodes()
    {
        var video = new ThreeCatVideo { Channels = new ThreeCatVideoChannels { Channels = [new ThreeCatLabel { Id = "PUC_S3" }] } };
        Assert.False(DynamicEpisodePolicy.HasExplicitChildTarget(video));
        video.Targets = new ThreeCatTargets { Targets = [new ThreeCatLabel { Id = "PUT_INF" }] };
        Assert.True(DynamicEpisodePolicy.HasExplicitChildTarget(video));
    }
}
