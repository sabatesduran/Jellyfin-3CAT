using Jellyfin.Plugin.ThreeCat.Library;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>One canonical parent per title: never make aliases of series or movies.</summary>
internal static class NativeCatalogueLayout
{
    internal sealed record Category(string Id, string Name);
    internal static readonly Category[] Categories =
    [
        new("cat:series", "Sèries"),
        new("cat:cinema", "Cinema"),
        new("cat:documentals", "Documentals"),
        new("cat:anime", "Anime"),
        new("cat:sx3", "SX3"),
        new("cat:programes", "Programes")
    ];

    internal static bool IsCategory(string? id) => Categories.Any(c => c.Id == id);

    internal static string CategoryFor(ChannelItemInfo item)
    {
        if (item.Type == ChannelItemType.Media && item.ContentType == ChannelMediaContentType.Movie) return "cat:cinema";
        if (item.Genres.Contains(ProgramClassifier.Anime, StringComparer.OrdinalIgnoreCase)) return "cat:anime";
        if (item.Tags.Contains("SX3", StringComparer.OrdinalIgnoreCase) || item.Genres.Contains(ProgramClassifier.Children)) return "cat:sx3";
        if (item.Genres.Contains(ProgramClassifier.Documentary)) return "cat:documentals";
        if (item.Genres.Contains(ProgramClassifier.Series)) return "cat:series";
        if (item.Genres.Contains(ProgramClassifier.Cinema)) return "cat:cinema";
        return "cat:programes";
    }

    internal static string AssignedCategory(ChannelItemInfo item, IReadOnlyDictionary<string, string> existing)
        => existing.TryGetValue(item.Id, out var category) && IsCategory(category) ? category : CategoryFor(item);

    internal static ChannelItemInfo[] Root(IReadOnlyList<ChannelItemInfo> catalogue,
        IReadOnlyDictionary<string, string> existing, IReadOnlySet<string> stillAtRoot)
    {
        var folders = Categories.Select(c => new ChannelItemInfo
        {
            Id = c.Id, Name = c.Name, Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container, MediaType = ChannelMediaType.Video,
            ImageUrl = catalogue.Where(i => AssignedCategory(i, existing) == c.Id)
                .Select(i => i.ImageUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))!,
            Overview = "Explora " + c.Name + " de 3Cat."
        });
        // Keep legacy root entries until native ChannelManager has moved them into a category.
        return folders.Concat(catalogue.Where(i => stillAtRoot.Contains(i.Id))).ToArray();
    }

    internal static ChannelItemInfo[] Contents(string category, IReadOnlyList<ChannelItemInfo> catalogue,
        IReadOnlyDictionary<string, string> existing)
        => catalogue.Where(i => AssignedCategory(i, existing) == category).ToArray();
}
