using Jellyfin.Plugin.ThreeCat.Dynamic;
using Jellyfin.Plugin.ThreeCat.ThreeCat;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class DynamicPlaybackQualityTests
{
    [Fact]
    public void SelectsActiveQualityAtOrBelowPreference()
    {
        var media = new ThreeCatMedia { Playback = new ThreeCatPlayback { Files =
        [
            new ThreeCatMediaFile { File = "https://example.org/1080.mp4", Label = "1080p", Active = true },
            new ThreeCatMediaFile { File = "https://example.org/720.mp4", Label = "720p", Active = true },
            new ThreeCatMediaFile { File = "https://example.org/480.mp4", Label = "480p", Active = false }
        ] } };
        Assert.Equal("https://example.org/720.mp4", DynamicPlaybackQuality.Select(media, "720"));
        Assert.Equal("https://example.org/1080.mp4", DynamicPlaybackQuality.Select(media, "auto"));
        Assert.Null(DynamicPlaybackQuality.Select(null, "720"));
    }

    [Fact]
    public void CandidatesExposeDistinctActiveHttpsSources()
    {
        var media = new ThreeCatMedia { Playback = new ThreeCatPlayback { Files =
        [
            new ThreeCatMediaFile { File = "https://example.org/1080.mp4", Label = "1080p", Active = true },
            new ThreeCatMediaFile { File = "https://example.org/720.mp4", Label = "720p", Active = true },
            new ThreeCatMediaFile { File = "https://example.org/720.mp4", Label = "720p", Active = true },
            new ThreeCatMediaFile { File = "http://example.org/480.mp4", Label = "480p", Active = true },
            new ThreeCatMediaFile { File = "https://example.org/360.mp4", Label = "360p", Active = false }
        ] } };
        Assert.Equal(new[] { "720p", "1080p" }, DynamicPlaybackQuality.Candidates(media, "720").Select(x => x.Label));
    }

    [Fact]
    public void MovieIdsAreSeparateFromProgrammeAndEpisodeIds()
    {
        Assert.True(DynamicIds.TryFilm("m:12345", out var id));
        Assert.Equal("12345", id);
        Assert.False(DynamicIds.TryFilm("v:12345:12345", out _));
        Assert.False(DynamicIds.TryFilm("m:../secret", out _));
    }
}
