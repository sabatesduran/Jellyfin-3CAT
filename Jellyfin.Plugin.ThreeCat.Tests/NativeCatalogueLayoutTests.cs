using Jellyfin.Plugin.ThreeCat.Dynamic;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class NativeCatalogueLayoutTests
{
    private static ChannelItemInfo Programme(string id, params string[] genres) => new()
    {
        Id = "p:" + id, Name = id, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
        Genres = genres.ToList()
    };

    [Fact]
    public void EveryTitleHasOneCanonicalParent()
    {
        var items = new[] { Programme("1", "Anime", "Infantil"), Programme("2", "Sèrie"),
            Programme("3", "Documental"), Programme("4"),
            new ChannelItemInfo { Id = "m:5", Type = ChannelItemType.Media, ContentType = ChannelMediaContentType.Movie } };
        var assigned = new Dictionary<string, string>();
        var all = NativeCatalogueLayout.Categories.SelectMany(c => NativeCatalogueLayout.Contents(c.Id, items, assigned)).ToArray();
        Assert.Equal(items.Length, all.Length);
        Assert.Equal(items.Select(i => i.Id).OrderBy(i => i), all.Select(i => i.Id).OrderBy(i => i));
        Assert.Equal("cat:anime", NativeCatalogueLayout.CategoryFor(items[0]));
        Assert.Equal("cat:cinema", NativeCatalogueLayout.CategoryFor(items[4]));
    }

    [Fact]
    public void UpgradeKeepsRootEntriesUntilMovedAndRetainsCanonicalIds()
    {
        var series = Programme("123", "Sèrie");
        var entries = new[] { series };
        var existing = new Dictionary<string, string>();
        Assert.Contains(series, NativeCatalogueLayout.Root(entries, existing, new HashSet<string> { series.Id }));
        Assert.Same(series, Assert.Single(NativeCatalogueLayout.Contents("cat:series", entries, existing)));
        existing[series.Id] = "cat:series";
        Assert.DoesNotContain(series, NativeCatalogueLayout.Root(entries, existing, new HashSet<string>()));
        series.Genres = ["Documental"];
        Assert.Same(series, Assert.Single(NativeCatalogueLayout.Contents("cat:series", entries, existing)));
        Assert.Empty(NativeCatalogueLayout.Contents("cat:documentals", entries, existing));
    }

    [Fact]
    public void EmptyCategoriesRemainStableAndUnknownIdsAreRejected()
    {
        Assert.Equal(6, NativeCatalogueLayout.Root([], new Dictionary<string, string>(), new HashSet<string>()).Length);
        Assert.False(NativeCatalogueLayout.IsCategory("cat:unknown"));
    }
}
