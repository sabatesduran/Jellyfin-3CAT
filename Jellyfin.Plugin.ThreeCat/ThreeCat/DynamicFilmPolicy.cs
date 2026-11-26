using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThreeCat.ThreeCat;

internal static class DynamicFilmPolicy
{
    internal static bool IsFilm(ThreeCatVideo video)
    {
        if (string.IsNullOrWhiteSpace(video.Id) || string.IsNullOrWhiteSpace(video.Title)) return false;
        if (video.Typology is { Length: > 0 } type && !type.Equals("DTY_VIDEO_MM", StringComparison.OrdinalIgnoreCase)) return false;
        if (video.Formats is { Count: > 0 } && !video.Formats.Any(f => string.Equals(f.Id, "PUFF_FILM", StringComparison.OrdinalIgnoreCase))) return false;
        return !IsPromo(video.Title) && !IsPromo(video.Antetitle);
    }

    private static bool IsPromo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var plain = string.Concat(value.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).Trim().ToLowerInvariant();
        return Regex.IsMatch(plain, @"^(?:trailer|teaser|fragment|clip|promocio|promo|avancament)(?:\b|:)")
            || Regex.IsMatch(plain, @"(?:\(|\s[-–—]\s)(?:trailer|teaser|fragment|clip|promo)(?:\)|$)");
    }
}
