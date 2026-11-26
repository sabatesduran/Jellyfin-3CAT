using Jellyfin.Plugin.ThreeCat.Dynamic;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class ThreeCatChannelArtworkTests
{
    [Theory]
    [InlineData(ImageType.Primary)]
    [InlineData(ImageType.Thumb)]
    public void ProvidesIndependentPngStreams(ImageType type)
    {
        Assert.Contains(type, ThreeCatChannelArtwork.Supported);
        var image = ThreeCatChannelArtwork.Get(type);
        Assert.True(image.HasImage);
        Assert.Equal(ImageFormat.Png, image.Format);
        using var stream = image.Stream;
        using var second = ThreeCatChannelArtwork.Get(type).Stream;
        Assert.NotSame(stream, second);
        var signature = new byte[8];
        stream.ReadExactly(signature);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, signature);
        Assert.Equal(0, second.Position);
    }

    [Fact]
    public void UnsupportedArtworkTypeIsNotAdvertised()
    {
        Assert.False(ThreeCatChannelArtwork.Get(ImageType.Backdrop).HasImage);
    }
}
