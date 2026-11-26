using System.Globalization;

namespace Jellyfin.Plugin.ThreeCat.ThreeCat;

internal static class DynamicArtwork
{
    internal static string? SelectRole(IEnumerable<ThreeCatImage>? images, params string[] roles)
    {
        var valid = (images ?? []).Where(i => Uri.TryCreate(i.Url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps).ToArray();
        foreach (var role in roles)
        {
            var match = valid.Where(i => string.Equals(i.Relation, role, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(i => Area(i.Size)).FirstOrDefault();
            if (match is not null) return match.Url;
        }
        return null;
    }

    internal static string? Select(IEnumerable<ThreeCatImage>? images, params string[] roles) =>
        SelectRole(images, roles) ?? (images ?? []).Where(i => Uri.TryCreate(i.Url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps).OrderByDescending(i => Area(i.Size)).FirstOrDefault()?.Url;

    private static long Area(string? size)
    {
        if (string.Equals(size, "MASTER", StringComparison.OrdinalIgnoreCase)) return long.MaxValue;
        var parts = size?.ToLowerInvariant().Split('x');
        return parts is { Length: 2 } && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            ? (long)w * h : 0;
    }
}
