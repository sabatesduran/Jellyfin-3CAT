using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThreeCat.ThreeCat;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

internal static class DynamicPlaybackQuality
{
    internal sealed record Candidate(string Url, string Label);

    /// <summary>Expose distinct playable HTTPS sources, preferred quality first. No fabricated variants.</summary>
    internal static IReadOnlyList<Candidate> Candidates(ThreeCatMedia? media, string? quality)
    {
        if (media?.Info?.Status?.Active == false || media?.Info?.Expires <= DateTimeOffset.UtcNow) return [];
        var files = media?.Playback?.Files;
        if (files is null) return [];
        var valid = files.Where(f => Uri.TryCreate(f.File, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps).ToArray();
        var active = valid.Where(f => f.Active).ToArray();
        // Some older responses omit the active flag. Keep the broadcaster's primary fallback only.
        if (active.Length == 0) active = valid.Take(1).ToArray();
        var preferred = Select(media, quality);
        return active.OrderByDescending(f => string.Equals(f.File, preferred, StringComparison.Ordinal))
            .ThenByDescending(f => Height(f.Label))
            .DistinctBy(f => f.File, StringComparer.Ordinal)
            .Take(4).Select((f, i) => new Candidate(f.File!, string.IsNullOrWhiteSpace(f.Label) ? $"Font {i + 1}" : f.Label.Trim()))
            .ToArray();
    }

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
