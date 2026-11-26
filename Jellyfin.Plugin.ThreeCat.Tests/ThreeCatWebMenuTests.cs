using System.Text.Json.Nodes;
using Jellyfin.Plugin.ThreeCat.Portal;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public class ThreeCatWebMenuTests
{
    [Fact]
    public void AddsOneLinkWithoutLosingExistingConfiguration()
    {
        const string original = "{\"menuLinks\":[{\"name\":\"Other\",\"url\":\"/other\"}],\"multiserver\":true}";
        const string url = "/jellyfin/ThreeCat/Explore/";
        var first = ThreeCatWebMenu.AddLink(original, url)!;
        var second = ThreeCatWebMenu.AddLink(first, url)!;
        var json = JsonNode.Parse(second)!;
        Assert.Equal(2, json["menuLinks"]!.AsArray().Count);
        Assert.True(json["multiserver"]!.GetValue<bool>());
        Assert.Equal(url, json["menuLinks"]![1]!["url"]!.GetValue<string>());
        Assert.Equal(first, second);
    }

    [Fact]
    public void UnexpectedExistingStructureIsPreserved()
        => Assert.Null(ThreeCatWebMenu.AddLink("{\"menuLinks\":false}", "/ThreeCat/Explore/"));
}
