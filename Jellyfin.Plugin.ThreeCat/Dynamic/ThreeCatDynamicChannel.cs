using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThreeCat.Sync;
using Jellyfin.Plugin.ThreeCat.Library;
using Jellyfin.Plugin.ThreeCat.ThreeCat;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Native, on-demand 3Cat channel. No STRM files are needed.</summary>
public class ThreeCatDynamicChannel : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    private readonly ThreeCatClient _client;
    private readonly IMediaEncoder _encoder;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ThreeCatDynamicChannel> _logger;
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly SemaphoreSlim EpisodeGate = new(3, 3);
    private static readonly ConcurrentDictionary<string, Snapshot<EpisodeEntry>> Episodes = new(StringComparer.Ordinal);
    private static Snapshot<ChannelItemInfo>? _catalog;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    public ThreeCatDynamicChannel(ThreeCatClient client, IMediaEncoder encoder, IApplicationPaths paths, ILogger<ThreeCatDynamicChannel> logger)
    {
        _client = client;
        _encoder = encoder;
        _paths = paths;
        _logger = logger;
    }

    protected virtual bool ChildrenOnly => false;
    public virtual string Name => "3Cat a la carta";
    public virtual string Description => "Catàleg de 3Cat amb episodis sota demanda.";
    public string DataVersion => "1";
    public string HomePageUrl => "https://www.3cat.cat/3cat/";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.UsR;
    public virtual bool IsEnabledFor(string userId) => Plugin.Instance?.Configuration.DynamicCatalogue == true;
    public string GetCacheKey(string? userId) => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 1800).ToString(CultureInfo.InvariantCulture);

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video],
        ContentTypes = ChildrenOnly ? [ChannelMediaContentType.Episode] : [ChannelMediaContentType.Episode, ChannelMediaContentType.Movie],
        SupportsContentDownloading = false
    };

    public virtual IEnumerable<ImageType> GetSupportedChannelImages() => ThreeCatChannelArtwork.Supported;
    public virtual Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(ThreeCatChannelArtwork.Get(type));

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        // Throw rather than return an empty authoritative list on disable: Jellyfin may reconcile deletions.
        if (!IsEnabledFor(query.UserId.ToString()))
            throw new InvalidOperationException("El canal 3Cat està desactivat.");

        IReadOnlyList<ChannelItemInfo> items;
        var folder = query.FolderId;
        if (ChildrenOnly && !string.IsNullOrEmpty(folder))
        {
            if (!folder.StartsWith("sx3:", StringComparison.Ordinal)) throw new ArgumentException("Identificador SX3 no vàlid.");
            folder = folder[4..];
            if ((!DynamicIds.TryProgram(folder, out var childProgram) && !DynamicIds.TrySeason(folder, out childProgram, out _))
                || !(await GetCatalogueAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == DynamicIds.Program(childProgram) && p.Tags.Contains("SX3")))
                throw new ArgumentException("Programa fora del catàleg SX3.");
        }
        if (string.IsNullOrEmpty(folder))
        {
            items = await GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (DynamicIds.TryProgram(folder, out var program))
        {
            var episodes = await GetEpisodesAsync(program, cancellationToken).ConfigureAwait(false);
            var title = await GetProgrammeNameAsync(program, cancellationToken).ConfigureAwait(false);
            items = episodes.Where(IsAvailable).GroupBy(e => e.Season).OrderBy(g => g.Key)
                .Select(g => new ChannelItemInfo
                {
                    Id = DynamicIds.Season(program, g.Key),
                    Name = g.Key == 0 ? "Especials" : $"Temporada {g.Key}",
                    SeriesName = title,
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
            var title = await GetProgrammeNameAsync(program, cancellationToken).ConfigureAwait(false);
            items = episodes.Where(e => e.Season == season && IsAvailable(e))
                .OrderBy(e => e.Number).ThenBy(e => e.Part).ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new ChannelItemInfo
                {
                    Id = DynamicIds.Video(program, e.Id),
                    Name = e.Part > 0 ? $"{e.Title} (part {e.Part})" : e.Title,
                    SeriesName = title,
                    Type = ChannelItemType.Media,
                    ContentType = ChannelMediaContentType.Episode,
                    MediaType = ChannelMediaType.Video,
                    IndexNumber = e.Number,
                    ParentIndexNumber = e.Season,
                    PremiereDate = e.Aired?.UtcDateTime,
                    RunTimeTicks = e.Duration?.Ticks,
                    ImageUrl = e.Image!,
                    Overview = e.Overview!,
                    DateCreated = e.Aired?.UtcDateTime,
                    EndDate = e.Expires?.UtcDateTime,
                    ProviderIds = new Dictionary<string, string> { ["threecat"] = e.Id }
                }).ToArray();
        }
        else
        {
            throw new ArgumentException("Identificador de carpeta 3Cat no vàlid.");
        }

        if (ChildrenOnly)
        {
            if (string.IsNullOrEmpty(folder)) items = items.Where(p => p.Type == ChannelItemType.Folder && p.Tags.Contains("SX3")).ToArray();
            items = items.Select(PrefixChildItem).ToArray();
        }
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private static ChannelItemInfo PrefixChildItem(ChannelItemInfo item)
    {
        // Root metadata belongs to the shared cache. Copy it before adding the channel namespace.
        var copy = JsonSerializer.Deserialize<ChannelItemInfo>(JsonSerializer.Serialize(item))!;
        copy.Id = "sx3:" + item.Id;
        return copy;
    }

    private async Task<string> GetProgrammeNameAsync(string program, CancellationToken ct)
    {
        var catalogue = await GetCatalogueAsync(ct).ConfigureAwait(false);
        return catalogue.FirstOrDefault(p => p.Id == DynamicIds.Program(program))?.Name ?? program;
    }

    private static bool IsAvailable(EpisodeEntry entry) => entry.Expires is null || entry.Expires > DateTimeOffset.UtcNow;

    private async Task<IReadOnlyList<ChannelItemInfo>> GetCatalogueAsync(CancellationToken ct)
    {
        await CatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog is null) _catalog = ReadDiskCatalogue();
            if (_catalog is { } current && current.Fresh)
            {
                if (DynamicStatus.Current.LastSuccess is null)
                {
                    DynamicStatus.Success(current.Items.Count(p => p.Type == ChannelItemType.Folder),
                        current.Items.Count(p => p.ContentType == ChannelMediaContentType.Movie), current.Items.Count(p => p.Tags.Contains("SX3")));
                }
                return current.Items;
            }
            try
            {
                var programs = await _client.ListCompleteProgramsAsync(ct).ConfigureAwait(false);
                IReadOnlyDictionary<string, ThreeCatProgramTaxonomy>? taxonomy = null;
                if (Plugin.Instance?.Configuration.ClassifyPrograms == true)
                {
                    try
                    {
                        taxonomy = await _client.GetProgramTaxonomyAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "3Cat: no s'ha pogut carregar la classificació.");
                    }
                }
                var fresh = programs.Where(p => p.HasVideo && DynamicIds.Valid(p.Id))
                    .DistinctBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new ChannelItemInfo
                    {
                        Id = DynamicIds.Program(p.Id), Name = p.Title,
                        Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
                        MediaType = ChannelMediaType.Video, HomePageUrl = p.Url!,
                        ImageUrl = p.ImageUrl!,
                        Overview = PlainText(p.Description)!,
                        Tags = p.Children ? ["SX3"] : [],
                        Genres = GetProgrammeGenres(p, taxonomy),
                        Studios = string.IsNullOrWhiteSpace(p.Channel) ? [] : [p.Channel],
                        PremiereDate = DateTime.TryParseExact(p.Published, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published.ToUniversalTime() : null,
                        ProviderIds = new Dictionary<string, string> { ["threecat"] = p.Id }
                    }).ToArray();
                if (fresh.Length == 0) throw new InvalidOperationException("Catàleg de 3Cat buit o no disponible.");
                var films = (_catalog?.Items ?? []).Where(p => p.ContentType == ChannelMediaContentType.Movie && (p.EndDate is null || p.EndDate > DateTime.UtcNow)).ToArray();
                string? filmError = null;
                try
                {
                    var videos = await _client.ListFilmsAsync(ct).ConfigureAwait(false);
                    films = videos.Where(v => DynamicIds.Valid(v.Id)).Select(v => new ChannelItemInfo
                    {
                        Id = DynamicIds.Film(v.Id!), Name = v.Title ?? v.FriendlyName ?? v.Id!,
                        Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                        ContentType = ChannelMediaContentType.Movie, Genres = ["Cinema"],
                        Overview = PlainText(v.Description)!, ImageUrl = ThreeCatSyncService.PickThumbnail(v, null)!,
                        PremiereDate = (v.Broadcast ?? v.Published)?.UtcDateTime,
                        DateCreated = v.Published?.UtcDateTime, EndDate = v.Expires?.UtcDateTime, RunTimeTicks = v.Duration?.Ticks,
                        ProviderIds = new Dictionary<string, string> { ["threecat"] = v.Id! }
                    }).ToArray();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    filmError = "No s'ha pogut actualitzar el cinema.";
                    _logger.LogWarning(ex, "3Cat: es conserva el cinema anterior.");
                    // An unavailable optional cinema catalogue must not block the programmes.
                }
                var previous = _catalog?.Items.Where(p => p.Type == ChannelItemType.Folder).ToArray() ?? [];
                if (previous.Length > 0 && fresh.Length < previous.Length * .9)
                    throw new InvalidOperationException("3Cat: reducció sobtada del catàleg; es conserva la còpia anterior.");
                var merged = fresh.Concat(films).OrderBy(p => p.Name,
                    StringComparer.Create(CultureInfo.GetCultureInfo("ca-ES"), true)).ToArray();
                _catalog = new Snapshot<ChannelItemInfo>(DateTimeOffset.UtcNow + Ttl, merged);
                WriteDiskCatalogue(_catalog);
                DynamicStatus.Success(fresh.Length, films.Length, fresh.Count(p => p.Tags.Contains("SX3")));
                if (filmError is not null) DynamicStatus.Failure(filmError);
                return merged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _catalog is not null)
            {
                DynamicStatus.Failure("No s'ha pogut actualitzar el catàleg. Es conserva la còpia anterior.");
                _logger.LogWarning(ex, "3Cat: es conserva el catàleg anterior.");
                return _catalog.Items;
            }
        }
        finally { CatalogGate.Release(); }
    }

    private string CataloguePath => Path.Combine(_paths.CachePath, "3cat-dynamic-catalogue.json");

    private Snapshot<ChannelItemInfo>? ReadDiskCatalogue()
    {
        try
        {
            if (!File.Exists(CataloguePath) || new FileInfo(CataloguePath).Length > 32_000_000) return null;
            var snapshot = JsonSerializer.Deserialize<Snapshot<ChannelItemInfo>>(File.ReadAllText(CataloguePath));
            if (snapshot?.Items is not { Count: > 0 }) return null;
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "3Cat: no s'ha pogut llegir la memòria cau.");
            return null;
        }
    }

    private void WriteDiskCatalogue(Snapshot<ChannelItemInfo> snapshot)
    {
        var temporary = CataloguePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_paths.CachePath);
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, CataloguePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "3Cat: no s'ha pogut desar la memòria cau.");
        }
    }

    private static string? PlainText(string? html) => string.IsNullOrWhiteSpace(html) ? null : WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", string.Empty)).Trim();

    private static List<string> GetProgrammeGenres(ThreeCatCatalogProgram program, IReadOnlyDictionary<string, ThreeCatProgramTaxonomy>? taxonomy)
    {
        var genres = new List<string>();
        if (program.Children) genres.Add("Infantil");
        if (taxonomy is not null && taxonomy.TryGetValue(program.Id, out var facets))
        {
            genres.AddRange(ProgramClassifier.Classify(facets.Formats, facets.Themes, null).Genres);
        }
        return genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                    ThreeCatSyncService.PickThumbnail(v, null), slots[i].IsDuplicate, PlainText(v.Description)))
                    .Where(e => !e.Duplicate).ToArray();
                var merged = fresh;
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
        if (!IsEnabledFor(string.Empty)) return [];
        var originalId = id;
        if (ChildrenOnly)
        {
            if (!id.StartsWith("sx3:", StringComparison.Ordinal)) return [];
            id = id[4..];
            var parts = id.Split(':');
            if (parts.Length != 3 || !(await GetCatalogueAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == DynamicIds.Program(parts[1]) && p.Tags.Contains("SX3"))) return [];
        }
        if (!DynamicIds.TryVideo(id, out var video) && (ChildrenOnly || !DynamicIds.TryFilm(id, out video))) return [];
        var media = await _client.GetMediaAsync(video, cancellationToken).ConfigureAwait(false);
        var url = DynamicPlaybackQuality.Select(media, Plugin.Instance?.Configuration.DynamicQuality);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return [];
        var source = new MediaSourceInfo
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalId)))[..32].ToLowerInvariant(),
            Path = uri.AbsoluteUri, Name = "3Cat", Protocol = MediaProtocol.Http,
            Container = "mp4", IsRemote = true, RunTimeTicks = media?.Info?.Duration?.Ticks,
            SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, SupportsProbing = true
        };

        // Probe only when requested, rather than inventing codecs or probing the entire catalogue.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var info = await _encoder.GetMediaInfo(new MediaInfoRequest
            {
                MediaSource = source, MediaType = DlnaProfileType.Video, ExtractChapters = false
            }, timeout.Token).ConfigureAwait(false);
            source.MediaStreams = info.MediaStreams;
            source.SupportsDirectPlay = info.MediaStreams.Any(s => s.Type == MediaStreamType.Video) && !string.IsNullOrWhiteSpace(info.Container);
            source.Container = info.Container ?? source.Container;
            source.Formats = info.Formats;
            source.Bitrate = info.Bitrate;
            source.RunTimeTicks ??= info.RunTimeTicks;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "3Cat: no s'ha pogut analitzar el flux del vídeo {Video}.", video);
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (Plugin.Instance?.Configuration.WriteSubtitles == true && media?.Subtitles is { Count: > 0 } tracks)
        {
            var streams = source.MediaStreams.ToList();
            var nextIndex = streams.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
            foreach (var track in ThreeCatSyncService.SelectSubtitleTracks(tracks))
            {
                // Only use HTTPS sources supplied by 3Cat. Local cache names never contain remote paths.
                if (!Uri.TryCreate(track.Source, UriKind.Absolute, out var subtitleUri)
                    || subtitleUri.Scheme != Uri.UriSchemeHttps
                    || track.Language.Length is < 1 or > 8
                    || !track.Language.All(char.IsAsciiLetterOrDigit)) continue;
                try
                {
                    var directory = Path.Combine(_paths.CachePath, "3cat-dynamic-subtitles");
                    Directory.CreateDirectory(directory);
                    var filename = Path.Combine(directory, $"{video}.{track.Language}.vtt");
                    if (!File.Exists(filename) || File.GetLastWriteTimeUtc(filename) < DateTime.UtcNow.AddDays(-1))
                    {
                        var vtt = await _client.GetStringAsync(subtitleUri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(vtt) || !vtt.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("WEBVTT", StringComparison.Ordinal)
                            || vtt.Length > 4_000_000) continue;
                        var temp = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            await File.WriteAllTextAsync(temp, vtt, cancellationToken).ConfigureAwait(false);
                            File.Move(temp, filename, overwrite: true);
                        }
                        finally { if (File.Exists(temp)) File.Delete(temp); }
                    }
                    streams.Add(new MediaStream
                    {
                        Type = MediaStreamType.Subtitle, Codec = "webvtt",
                        Language = track.Language, Index = nextIndex++, Path = filename,
                        IsExternal = true, IsExternalUrl = false, SupportsExternalStream = true
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "3Cat: no s'ha pogut preparar un subtítol del vídeo {Video}.", video);
                }
            }
            source.MediaStreams = streams;
        }

        return [source];
    }

    private sealed record Snapshot<T>(DateTimeOffset Expires, IReadOnlyList<T> Items)
    {
        public bool Fresh => DateTimeOffset.UtcNow < Expires;
    }

    private sealed record EpisodeEntry(string Id, int Season, int Number, int Part, string Title,
        DateTimeOffset? Aired, DateTimeOffset? Expires, TimeSpan? Duration, string? Image, bool Duplicate, string? Overview);
}
