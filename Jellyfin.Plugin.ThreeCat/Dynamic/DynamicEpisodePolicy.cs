using Jellyfin.Plugin.ThreeCat.ThreeCat;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Explicit audience classification. A channel association alone does not establish audience safety.</summary>
internal static class DynamicEpisodePolicy
{
    internal static bool HasExplicitChildTarget(ThreeCatVideo video)
        => video.Targets?.Targets?.Any(t => string.Equals(t.Id, "PUT_INF", StringComparison.OrdinalIgnoreCase)) == true
            && DynamicAgeRating.FromLabels(video.AgeRatings) is "0" or "7";

    internal static bool IsAvailable(DateTimeOffset? expires, DateTimeOffset now)
        => expires is null || expires > now;
}
