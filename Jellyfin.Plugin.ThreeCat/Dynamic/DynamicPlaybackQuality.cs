using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThreeCat.ThreeCat;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

internal static class DynamicPlaybackQuality
{
    internal static string? Select(ThreeCatMedia? media, string? quality)
    {
        if (quality is null or "" or "auto") return ThreeCatClient.PickFile(media);
        if (!int.TryParse(quality, NumberStyles.None, CultureInfo.InvariantCulture, out var max) || max < 1)
            return ThreeCatClient.PickFile(media);
        var choices = media?.Playback?.Files?.Where(f => f.Active && !string.IsNullOrWhiteSpace(f.File))
            .Select(f => (File:f.File, Height: Height(f.Label))).Where(f => f.Height > 0).ToArray() ?? [];
        if (choices.Length == 0) return ThreeCatClient.PickFile(media);
        return choices.Where(f => f.Height <= max).OrderByDescending(f => f.Height).FirstOrDefault().File
            ?? choices.OrderBy(f => f.Height).First().File;
    }

    private static int Height(string? label)
    {
        var match = Regex.Match(label ?? "", @"(?<!\d)(\d{3,4})\s*p\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
}
