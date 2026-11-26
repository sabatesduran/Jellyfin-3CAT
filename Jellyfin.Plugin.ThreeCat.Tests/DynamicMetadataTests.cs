using Jellyfin.Plugin.ThreeCat.ThreeCat;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class DynamicMetadataTests
{
    [Fact]
    public void RatingUsesHighestExplicitAgeAndNeverGuesses()
    {
        Assert.Equal("0", DynamicAgeRating.FromLabels([new ThreeCatLabel { Id = "CE_TP" }]));
        Assert.Equal("16", DynamicAgeRating.FromLabels([new ThreeCatLabel { Id = "CE_07" }, new ThreeCatLabel { Id = "CE_16" }]));
        Assert.Equal("12", DynamicAgeRating.FromDescription("12 anys"));
        Assert.Null(DynamicAgeRating.FromDescription("Infantil"));
        Assert.Null(DynamicAgeRating.FromLabels([new ThreeCatLabel { Id = "CE_UNKNOWN" }]));
    }

    [Fact]
    public void ArtworkPrefersExplicitRoleOverLargerOtherImage()
    {
        var images = new[]
        {
            new ThreeCatImage { Url = "https://example.org/square", Relation = "IMAGE", Size = "1400x1400" },
            new ThreeCatImage { Url = "https://example.org/poster", Relation = "IMG_POSTER", Size = "288x422" },
            new ThreeCatImage { Url = "http://example.org/invalid", Relation = "IMG_POSTER", Size = "MASTER" }
        };
        Assert.Equal("https://example.org/poster", DynamicArtwork.Select(images, "IMG_POSTER", "IMAGE"));
        Assert.Null(DynamicArtwork.SelectRole(images, "IMG_LOGO"));
    }
}
