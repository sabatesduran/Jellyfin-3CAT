using System.Collections.Concurrent;
using System.Globalization;
using Jellyfin.Plugin.ThreeCat.Sync;
using Jellyfin.Plugin.ThreeCat.ThreeCat;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Native, on-demand 3Cat channel. No STRM files are needed.</summary>
public sealed class ThreeCatDynamicChannel : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    private readonly ThreeCatClient _client;
    private readonly ILogger<ThreeCatDynamicChannel> _logger;
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly SemaphoreSlim EpisodeGate = new(3, 3);
    private static readonly ConcurrentDictionary<string, Snapshot<EpisodeEntry>> Episodes = new(StringComparer.Ordinal);
    private static Snapshot<ChannelItemInfo>? _catalog;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public ThreeCatDynamicChannel(ThreeCatClient client, ILogger<ThreeCatDynamicChannel> logger)
    {
        _client = client;
        _logger = logger;
    }

    public string Name => "3Cat a la carta";
    public string Description => "Catàleg de 3Cat amb episodis sota demanda.";
    public string DataVersion => "1";
    public string HomePageUrl => "https://www.3cat.cat/3cat/";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.UsR;
    public bool IsEnabledFor(string userId) => Plugin.Instance?.Configuration.DynamicCatalogue == true;
    public string GetCacheKey(string? userId) => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 1800).ToString(CultureInfo.InvariantCulture);

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video],
        ContentTypes = [ChannelMediaContentType.Episode],
        SupportsContentDownloading = false
    };

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // Throw rather than return an empty authoritative list on disable: Jellyfin may reconcile deletions.
        if (!IsEnabledFor(query.UserId.ToString()))
            throw new InvalidOperationException("El canal 3Cat està desactivat.");

        IReadOnlyList<ChannelItemInfo> items;
        var folder = query.FolderId;
        if (string.IsNullOrEmpty(folder))
        {
            items = await GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (DynamicIds.TryProgram(folder, out var program))
        {
            var episodes = await GetEpisodesAsync(program, cancellationToken).ConfigureAwait(false);
            items = episodes.Where(IsAvailable).GroupBy(e => e.Season).OrderBy(g => g.Key)
                .Select(g => new ChannelItemInfo
                {
                    Id = DynamicIds.Season(program, g.Key),
                    Name = g.Key == 0 ? "Especials" : $"Temporada {g.Key}",
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Season,
                    MediaType = ChannelMediaType.Video,
                    IndexNumber = g.Key,
                    ImageUrl = g.Select(e => e.Image).FirstOrDefault(s => s is not null)!
                }).ToArray();
        }
        else if (DynamicIds.TrySeason(folder, out program, out var season))
        {
            var episodes = await GetEpisodesAsync(program, cancellationToken).ConfigureAwait(false);
            items = episodes.Where(e => e.Season == season && IsAvailable(e))
                .OrderBy(e => e.Number).ThenBy(e => e.Part).ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new ChannelItemInfo
                {
                    Id = DynamicIds.Video(program, e.Id),
                    Name = e.Part > 0 ? $"{e.Title} (part {e.Part})" : e.Title,
                    Type = ChannelItemType.Media,
                    ContentType = ChannelMediaContentType.Episode,
                    MediaType = ChannelMediaType.Video,
                    IndexNumber = e.Number,
                    ParentIndexNumber = e.Season,
                    PremiereDate = e.Aired?.UtcDateTime,
                    RunTimeTicks = e.Duration?.Ticks,
                    ImageUrl = e.Image!,
                    ProviderIds = new Dictionary<string, string> { ["threecat"] = e.Id }
                }).ToArray();
        }
        else
        {
            throw new ArgumentException("Identificador de carpeta 3Cat no vàlid.");
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private static bool IsAvailable(EpisodeEntry entry) => entry.Expires is null || entry.Expires > DateTimeOffset.UtcNow;

    private async Task<IReadOnlyList<ChannelItemInfo>> GetCatalogueAsync(CancellationToken ct)
    {
        await CatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog is { } current && current.Fresh) return current.Items;
            try
            {
                var programs = await _client.ListCompleteProgramsAsync(ct).ConfigureAwait(false);
                var fresh = programs.Where(p => p.HasVideo && DynamicIds.Valid(p.Id))
                    .DistinctBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new ChannelItemInfo
                    {
                        Id = DynamicIds.Program(p.Id), Name = p.Title,
                        Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
                        MediaType = ChannelMediaType.Video, HomePageUrl = p.Url!,
                        ProviderIds = new Dictionary<string, string> { ["threecat"] = p.Id }
                    }).ToArray();
                if (fresh.Length == 0) throw new InvalidOperationException("Catàleg de 3Cat buit o no disponible.");
                var merged = (_catalog?.Items ?? []).Concat(fresh)
                    .GroupBy(p => p.Id, StringComparer.Ordinal).Select(g => g.Last())
                    .OrderBy(p => p.Name, StringComparer.Create(CultureInfo.GetCultureInfo("ca-ES"), true)).ToArray();
                _catalog = new Snapshot<ChannelItemInfo>(DateTimeOffset.UtcNow + Ttl, merged);
                return merged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _catalog is not null)
            {
                _logger.LogWarning(ex, "3Cat: es conserva el catàleg anterior.");
                return _catalog.Items;
            }
        }
        finally { CatalogGate.Release(); }
    }

    private async Task<IReadOnlyList<EpisodeEntry>> GetEpisodesAsync(string program, CancellationToken ct)
    {
        if (Episodes.TryGetValue(program, out var cached) && cached.Fresh) return cached.Items;
        await EpisodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Episodes.TryGetValue(program, out cached) && cached.Fresh) return cached.Items;
            try
            {
                var page = await _client.GetProgramVideosAsync(program, 0, ct, requireComplete: true).ConfigureAwait(false);
                if (page.Items.Count == 0) throw new InvalidOperationException("No s'ha pogut carregar el programa de 3Cat.");
                var videos = page.Items.Where(v => DynamicIds.Valid(v.Id))
                    .OrderBy(v => v.Broadcast ?? v.Published ?? DateTimeOffset.MinValue)
                    .ThenBy(v => v.Id, StringComparer.Ordinal).ToArray();
                var titles = videos.Select(v => v.Title ?? v.FriendlyName).ToArray();
                var slots = EpisodeNumbering.Assign(titles);
                var fresh = videos.Select((v, i) => new EpisodeEntry(v.Id!, slots[i].Season, slots[i].Episode, slots[i].Part,
                    EpisodeNumbering.StripNumber(titles[i] ?? v.Id!), v.Broadcast ?? v.Published, v.Expires, v.Duration,
                    ThreeCatSyncService.PickThumbnail(v, null), slots[i].IsDuplicate))
                    .Where(e => !e.Duplicate).ToArray();
                var merged = (cached?.Items ?? []).Concat(fresh).GroupBy(e => e.Id, StringComparer.Ordinal)
                    .Select(g => g.Last()).ToArray();
                if (Episodes.Count >= 128 && !Episodes.ContainsKey(program))
                {
                    var oldest = Episodes.OrderBy(x => x.Value.Expires).FirstOrDefault();
                    if (oldest.Key is not null) Episodes.TryRemove(oldest.Key, out _);
                }
                Episodes[program] = new Snapshot<EpisodeEntry>(DateTimeOffset.UtcNow + Ttl, merged);
                return merged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && cached is not null)
            {
                _logger.LogWarning(ex, "3Cat: es conserva el programa {Program}.", program);
                return cached.Items;
            }
        }
        finally { EpisodeGate.Release(); }
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        if (!IsEnabledFor(string.Empty) || !DynamicIds.TryVideo(id, out var video)) return [];
        var media = await _client.GetMediaAsync(video, cancellationToken).ConfigureAwait(false);
        var url = ThreeCatClient.PickFile(media);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return [];
        return [new MediaSourceInfo
        {
            Id = video, Path = uri.AbsoluteUri, Name = "3Cat", Protocol = MediaProtocol.Http,
            Container = "mp4", IsRemote = true, RunTimeTicks = media?.Info?.Duration?.Ticks,
            SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, SupportsProbing = true
        }];
    }

    private sealed record Snapshot<T>(DateTimeOffset Expires, IReadOnlyList<T> Items)
    {
        public bool Fresh => DateTimeOffset.UtcNow < Expires;
    }

    private sealed record EpisodeEntry(string Id, int Season, int Number, int Part, string Title,
        DateTimeOffset? Aired, DateTimeOffset? Expires, TimeSpan? Duration, string? Image, bool Duplicate);
}
