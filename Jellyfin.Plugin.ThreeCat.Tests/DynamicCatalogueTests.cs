using Jellyfin.Plugin.ThreeCat.Configuration;
using Jellyfin.Plugin.ThreeCat.Dynamic;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class DynamicCatalogueTests
{
    [Fact]
    public void DynamicModeDoesNotChangeExistingInstallations()
    {
        var config = new PluginConfiguration();
        Assert.False(config.DynamicCatalogue);
        Assert.False(config.DisableLegacyMirror);
    }

    [Fact]
    public void IdentifiersRemainStableAcrossTitleChanges()
    {
        Assert.Equal("p:12345", DynamicIds.Program("12345"));
        Assert.Equal("s:12345:0", DynamicIds.Season("12345", 0));
        Assert.Equal("v:12345:98765", DynamicIds.Video("12345", "98765"));
        Assert.True(DynamicIds.TryProgram("p:12345", out var program));
        Assert.Equal("12345", program);
        Assert.True(DynamicIds.TrySeason("s:12345:22", out program, out var season));
        Assert.Equal("12345", program);
        Assert.Equal(22, season);
        Assert.True(DynamicIds.TryVideo("v:12345:98765", out var video));
        Assert.Equal("98765", video);
        Assert.NotEqual(DynamicIds.Video("12345", "98765"), DynamicIds.Video("54321", "98765"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p:12345")]
    [InlineData("v:12345:../secret")]
    [InlineData("v:12345:98765:extra")]
    [InlineData("v:abc:12345")]
    [InlineData("v:12345:https://example.org")]
    public void InvalidPlaybackIdsAreRejected(string? id)
    {
        Assert.False(DynamicIds.TryVideo(id, out _));
    }

    [Theory]
    [InlineData("s:12345:-1")]
    [InlineData("s:12345:999999999999999")]
    [InlineData("s:abc:1")]
    public void InvalidSeasonIdsAreRejected(string id)
    {
        Assert.False(DynamicIds.TrySeason(id, out _, out _));
    }
}
