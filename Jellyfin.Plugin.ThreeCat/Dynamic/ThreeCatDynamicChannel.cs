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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Native, on-demand 3Cat channel. No STRM files are needed.</summary>
public abstract class ThreeCatChannelBase : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    private readonly ThreeCatClient _client;
    private readonly IMediaEncoder _encoder;
    private readonly IApplicationPaths _paths;
    private readonly ILogger<ThreeCatDynamicChannel> _logger;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly SemaphoreSlim EpisodeGate = new(3, 3);
    private static readonly SemaphoreSlim[] EpisodeLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static DateTimeOffset _catalogRetryAfter;
    private static string? _catalogSignature;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, Snapshot<EpisodeEntry>> Episodes = new(StringComparer.Ordinal);
    private static Snapshot<ChannelItemInfo>? _catalog;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    protected ThreeCatChannelBase(ThreeCatClient client, IMediaEncoder encoder, IApplicationPaths paths, ILogger<ThreeCatDynamicChannel> logger, ILibraryManager library, IUserManager users)
    {
        _client = client;
        _encoder = encoder;
        _paths = paths;
        _logger = logger;
        _library = library;
        _users = users;
    }

    protected virtual bool ChildrenOnly => false;
    public virtual string Name => "3Cat a la carta";
    public virtual string Description => "Catàleg de 3Cat amb episodis sota demanda.";
    public string DataVersion => "native-2";
    public string HomePageUrl => "https://www.3cat.cat/3cat/";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.UsR;
    public virtual bool IsEnabledFor(string userId) => Plugin.Instance?.Configuration.DynamicCatalogue == true;
    private static string CatalogueSignature()
    {
        var config = Plugin.Instance?.Configuration;
        return $"{config?.ClassifyPrograms}:{config?.SeparateAnime}:{config?.SeparateChildren}";
    }

    public string GetCacheKey(string? userId) =>
        $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 1800}:{CatalogueSignature()}:{userId}";

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

        EnsureUserAccess(query.UserId);
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
            var catalogue = await GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            if (ChildrenOnly) items = catalogue;
            else
            {
                var layout = ReadNativeLayout();
                items = NativeCatalogueLayout.Root(catalogue, layout.Assigned, layout.StillAtRoot);
            }
        }
        else if (!ChildrenOnly && NativeCatalogueLayout.IsCategory(folder))
        {
            var catalogue = await GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
            items = NativeCatalogueLayout.Contents(folder!, catalogue, ReadNativeLayout().Assigned);
        }
        else if (DynamicIds.TryProgram(folder, out var program))
        {
            var episodes = await GetEpisodesAsync(program, cancellationToken).ConfigureAwait(false);
            var title = await GetProgrammeNameAsync(program, cancellationToken).ConfigureAwait(false);
            items = episodes.Where(e => IsAvailable(e) && (!ChildrenOnly || e.ExplicitChild)).GroupBy(e => e.Season).OrderBy(g => g.Key)
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
            items = episodes.Where(e => e.Season == season && IsAvailable(e) && (!ChildrenOnly || e.ExplicitChild))
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
                    OfficialRating = e.Rating!,
                    Tags = ExpiryTags(e.Expires),
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

    // Native channels use a single parent per item. Read the actual stored hierarchy,
    // rather than generating alternate IDs or deleting existing root items during an upgrade.
    private (Dictionary<string, string> Assigned, HashSet<string> StillAtRoot) ReadNativeLayout()
    {
        var assigned = new Dictionary<string, string>(StringComparer.Ordinal);
        var stillAtRoot = new HashSet<string>(StringComparer.Ordinal);
        var channelId = _library.GetNewItemId("Channel " + Name, typeof(Channel));
        var root = _library.GetItemList(new InternalItemsQuery { ParentId = channelId, EnableTotalRecordCount = false });
        foreach (var item in root.Where(i => i.ChannelId == channelId))
        {
            if (NativeCatalogueLayout.IsCategory(item.ExternalId))
            {
                foreach (var child in _library.GetItemList(new InternalItemsQuery { ParentId = item.Id, EnableTotalRecordCount = false }))
                    if (child.ChannelId == channelId && !string.IsNullOrEmpty(child.ExternalId))
                        assigned[child.ExternalId] = item.ExternalId;
            }
            else if (DynamicIds.TryProgram(item.ExternalId, out _) || DynamicIds.TryFilm(item.ExternalId, out _))
                stillAtRoot.Add(item.ExternalId);
        }
        return (assigned, stillAtRoot);
    }

    private void EnsureUserAccess(Guid userId)
    {
        if (userId == Guid.Empty) return; // Jellyfin's internal scheduled indexing.
        var user = _users.GetUserById(userId);
        var channel = _library.GetItemById(_library.GetNewItemId("Channel " + Name, typeof(Channel))) as Channel;
        if (user is null || channel is null || !channel.IsVisible(user))
            throw new UnauthorizedAccessException("Canal 3Cat no accessible.");
    }

    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        if (!IsEnabledFor(request.UserId ?? string.Empty)) return [];
        if (Guid.TryParse(request.UserId, out var userId)) EnsureUserAccess(userId);
        if (ChildrenOnly) return [];
        var catalogue = await GetCatalogueAsync(cancellationToken).ConfigureAwait(false);
        return catalogue.Where(i => i.Type == ChannelItemType.Media && i.ContentType == ChannelMediaContentType.Movie)
            .OrderByDescending(i => i.DateCreated ?? i.PremiereDate).Take(50).ToArray();
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

    private static List<string> ExpiryTags(DateTimeOffset? expires) => expires is { } value
        ? ["3Cat:caduca:" + value.ToString("O", CultureInfo.InvariantCulture)] : [];

    private static bool IsAvailable(EpisodeEntry entry) => DynamicEpisodePolicy.IsAvailable(entry.Expires, DateTimeOffset.UtcNow);

    private static IReadOnlyList<ChannelItemInfo> AvailableCatalogue(IReadOnlyList<ChannelItemInfo> items) =>
        items.Where(p => p.EndDate is null || p.EndDate > DateTime.UtcNow).ToArray();

    private async Task<IReadOnlyList<ChannelItemInfo>> GetCatalogueAsync(CancellationToken ct)
    {
        await CatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var signature = CatalogueSignature();
            if (_catalog is null)
            {
                _catalog = ReadDiskCatalogue();
                _catalogSignature = _catalog?.Signature;
            }
            if (_catalogSignature != signature) _catalogRetryAfter = default;
            if (_catalog is { } current && ((current.Fresh && _catalogSignature == signature) || _catalogRetryAfter > DateTimeOffset.UtcNow))
            {
                if (DynamicStatus.Current.LastSuccess is null)
                {
                    DynamicStatus.Restore(current.Expires - Ttl, current.Items.Count(p => p.Type == ChannelItemType.Folder),
                        current.Items.Count(p => p.ContentType == ChannelMediaContentType.Movie), current.Items.Count(p => p.Tags.Contains("SX3")));
                }
                return AvailableCatalogue(current.Items);
            }
            try
            {
                var now = DateTimeOffset.UtcNow;
                var full = _catalog is null || _catalog.Signature != signature
                    || _catalog.LastFull <= now.AddHours(-24);
                var programs = full
                    ? await _client.ListCompleteProgramsAsync(ct).ConfigureAwait(false)
                    : await _client.ListRecentProgramsAsync(ct).ConfigureAwait(false);
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
                IReadOnlySet<string>? animeIds = null;
                if (taxonomy is not null && Plugin.Instance?.Configuration.SeparateAnime == true)
                {
                    try
                    {
                        var animation = taxonomy.Where(x => ProgramClassifier.IsAnimation(x.Value.Formats, x.Value.Themes))
                            .Select(x => x.Key);
                        var details = await _client.GetProgramDetailsAsync(animation, ct).ConfigureAwait(false);
                        animeIds = details.Where(x => ProgramClassifier.IsAnime(x.Value.Groups, x.Value.Countries))
                            .Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "3Cat: no s'ha pogut distingir l'anime.");
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
                        OfficialRating = DynamicAgeRating.FromDescription(p.AgeRating)!,
                        Tags = p.Children ? ["SX3"] : [],
                        Genres = GetProgrammeGenres(p, taxonomy, animeIds),
                        Studios = string.IsNullOrWhiteSpace(p.Channel) ? [] : [p.Channel],
                        PremiereDate = DateTime.TryParseExact(p.Published, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var published) ? published.ToUniversalTime() : null,
                        DateCreated = DateTime.TryParseExact(p.Published, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) ? created.ToUniversalTime() : null,
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
                        ContentType = ChannelMediaContentType.Movie, Genres = ["Cinema"], Tags = ExpiryTags(v.Expires),
                        Overview = PlainText(v.Description ?? v.Summary)!, OfficialRating = DynamicAgeRating.FromLabels(v.AgeRatings)!, ImageUrl = ThreeCatSyncService.PickThumbnail(v, null)!,
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
                var previous = _catalog?.Items ?? [];
                var programmes = full
                    ? DynamicReconciliation.Merge(previous.Where(p => p.Type == ChannelItemType.Folder),
                        fresh, _catalog?.Missing, p => p.Id)
                    : DynamicReconciliation.Upsert(previous.Where(p => p.Type == ChannelItemType.Folder),
                        fresh, _catalog?.Missing, p => p.Id);
                var movies = filmError is null
                    ? DynamicReconciliation.Merge(previous.Where(p => p.ContentType == ChannelMediaContentType.Movie),
                        films, _catalog?.Missing, p => p.Id)
                    : (Items: (IReadOnlyList<ChannelItemInfo>)films,
                        Missing: (_catalog?.Missing ?? new Dictionary<string, int>()).Where(x => x.Key.StartsWith("m:", StringComparison.Ordinal)).ToDictionary(x => x.Key, x => x.Value));
                var missing = programmes.Missing.Concat(movies.Missing).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
                var merged = programmes.Items.Concat(movies.Items).OrderBy(p => p.Name,
                    StringComparer.Create(CultureInfo.GetCultureInfo("ca-ES"), true)).ToArray();
                _catalog = new Snapshot<ChannelItemInfo>(DateTimeOffset.UtcNow + Ttl, merged, missing, signature, LastFull: full ? now : _catalog!.LastFull);
                _catalogSignature = signature;
                _catalogRetryAfter = default;
                WriteDiskCatalogue(_catalog);
                DynamicStatus.Success(programmes.Items.Count, movies.Items.Count, programmes.Items.Count(p => p.Tags.Contains("SX3")));
                if (filmError is not null) DynamicStatus.Failure(filmError);
                return AvailableCatalogue(merged);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DynamicStatus.Failure("No s'ha pogut actualitzar el catàleg. Es conserva la còpia anterior.");
                _logger.LogWarning(ex, "3Cat: es conserva el catàleg anterior.");
                if (_catalog is null) throw;
                _catalogRetryAfter = DateTimeOffset.UtcNow + RetryDelay;
                _catalogSignature = signature;
                return AvailableCatalogue(_catalog.Items);
            }
        }
        finally { CatalogGate.Release(); }
    }

    private string CataloguePath => Path.Combine(_paths.CachePath, "3cat-dynamic-catalogue.json");

    private Snapshot<ChannelItemInfo>? ReadDiskCatalogue()
    {
        var snapshot = DynamicSnapshotStore.Read<Snapshot<ChannelItemInfo>>(CataloguePath, _logger);
        return snapshot?.Items is { Count: > 0 } ? snapshot : null;
    }

    private void WriteDiskCatalogue(Snapshot<ChannelItemInfo> snapshot) =>
        DynamicSnapshotStore.Write(CataloguePath, snapshot, _logger);

    private string EpisodePath(string program) =>
        Path.Combine(_paths.CachePath, "3cat-dynamic-episodes-v2", program + ".json");

    private static string? PlainText(string? html) => string.IsNullOrWhiteSpace(html) ? null : WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", string.Empty)).Trim();

    private static List<string> GetProgrammeGenres(ThreeCatCatalogProgram program, IReadOnlyDictionary<string, ThreeCatProgramTaxonomy>? taxonomy, IReadOnlySet<string>? animeIds)
    {
        var genres = new List<string>();
        if (program.Children && Plugin.Instance?.Configuration.SeparateChildren != false) genres.Add("Infantil");
        if (taxonomy is not null && taxonomy.TryGetValue(program.Id, out var facets))
        {
            genres.AddRange(ProgramClassifier.Classify(facets.Formats, facets.Themes, null, anime: animeIds?.Contains(program.Id) == true).Genres);
        }
        return genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<IReadOnlyList<EpisodeEntry>> GetEpisodesAsync(string program, CancellationToken ct, bool requireFresh = false)
    {
        if (!DynamicIds.Valid(program)) throw new ArgumentException("Programa no vàlid.", nameof(program));
        if (Episodes.TryGetValue(program, out var cached) && cached.Fresh) return cached.Items;
        var gate = EpisodeLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(program) % (uint)EpisodeLocks.Length)];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Episodes.TryGetValue(program, out cached))
                cached = DynamicSnapshotStore.Read<Snapshot<EpisodeEntry>>(EpisodePath(program), _logger);
            if (cached is not null && cached.Fresh)
            {
                RememberEpisodes(program, cached);
                return cached.Items;
            }
            if (!requireFresh && cached?.RetryAfter > DateTimeOffset.UtcNow) return cached.Items;
            await EpisodeGate.WaitAsync(ct).ConfigureAwait(false);
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
                    ThreeCatSyncService.PickThumbnail(v, null), slots[i].IsDuplicate, PlainText(v.Description ?? v.Summary),
                    DynamicEpisodePolicy.HasExplicitChildTarget(v), DynamicAgeRating.FromLabels(v.AgeRatings)))
                    .Where(e => !e.Duplicate).ToArray();
                if (fresh.Length == 0) throw new InvalidOperationException("Resposta sense episodis vàlids.");
                var reconciled = DynamicReconciliation.Merge(cached?.Items ?? [], fresh, cached?.Missing, e => e.Id);
                // An absent episode can remain visible temporarily but loses its child-safety evidence.
                var merged = reconciled.Items.Select(e => reconciled.Missing.ContainsKey(e.Id) ? e with { ExplicitChild = false } : e).ToArray();
                var snapshot = new Snapshot<EpisodeEntry>(DateTimeOffset.UtcNow + Ttl, merged, reconciled.Missing);
                RememberEpisodes(program, snapshot);
                DynamicSnapshotStore.Write(EpisodePath(program), snapshot, _logger, 256);
                return merged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && cached is not null && !requireFresh)
            {
                _logger.LogWarning(ex, "3Cat: es conserva el programa {Program}.", program);
                RememberEpisodes(program, cached with { RetryAfter = DateTimeOffset.UtcNow + RetryDelay });
                return cached.Items;
            }
            finally { EpisodeGate.Release(); }
        }
        finally { gate.Release(); }
    }

    private static void RememberEpisodes(string program, Snapshot<EpisodeEntry> snapshot)
    {
        if (Episodes.Count >= 128 && !Episodes.ContainsKey(program))
        {
            var oldest = Episodes.OrderBy(x => x.Value.Expires).FirstOrDefault();
            if (oldest.Key is not null) Episodes.TryRemove(oldest.Key, out _);
        }
        Episodes[program] = snapshot;
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
            if (parts.Length != 3 || !DynamicIds.Valid(parts[1]) || !DynamicIds.Valid(parts[2])
                || !(await GetCatalogueAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == DynamicIds.Program(parts[1]) && p.Tags.Contains("SX3"))) return [];
        }
        var isEpisode = DynamicIds.TryVideo(id, out var video);
        if (!isEpisode && (ChildrenOnly || !DynamicIds.TryFilm(id, out video))) return [];
        if (isEpisode)
        {
            var program = id.Split(':')[1];
            var episodes = await GetEpisodesAsync(program, cancellationToken, requireFresh: ChildrenOnly).ConfigureAwait(false);
            if (!episodes.Any(e => e.Id == video && IsAvailable(e) && (!ChildrenOnly || e.ExplicitChild))) return [];
        }
        else if (!(await GetCatalogueAsync(cancellationToken).ConfigureAwait(false)).Any(p => p.Id == DynamicIds.Film(video))) return [];
        var media = await _client.GetMediaAsync(video, cancellationToken).ConfigureAwait(false);
        if (media?.Info?.Status?.Active == false || media?.Info?.Expires <= DateTimeOffset.UtcNow) return [];
        var candidates = DynamicPlaybackQuality.Candidates(media, Plugin.Instance?.Configuration.DynamicQuality);
        if (candidates.Count == 0) return [];

        var subtitles = await PrepareSubtitlesAsync(video, media, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var sources = await Task.WhenAll(candidates.Select((candidate, index) =>
            ProbeSourceAsync(originalId, video, candidate, index, media, subtitles, timeout.Token, cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return sources;
    }

    private static readonly SemaphoreSlim ProbeGate = new(4, 4);

    private async Task<MediaSourceInfo> ProbeSourceAsync(string originalId, string video,
        DynamicPlaybackQuality.Candidate candidate, int index, ThreeCatMedia? media,
        IReadOnlyList<CachedSubtitle> subtitles, CancellationToken probeToken, CancellationToken cancellationToken)
    {
        var source = new MediaSourceInfo
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{originalId}|{candidate.Label}|{index}")))[..32].ToLowerInvariant(),
            Path = candidate.Url, Name = $"3Cat · {candidate.Label}", Protocol = MediaProtocol.Http,
            Container = string.Equals(media?.Playback?.Format, "MP4", StringComparison.OrdinalIgnoreCase) ? "mp4" : null,
            IsRemote = true, RunTimeTicks = media?.Info?.Duration?.Ticks,
            SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = true, SupportsProbing = true
        };

        var acquired = false;
        try
        {
            await ProbeGate.WaitAsync(probeToken).ConfigureAwait(false);
            acquired = true;
            var info = await _encoder.GetMediaInfo(new MediaInfoRequest
            {
                MediaSource = source, MediaType = DlnaProfileType.Video, ExtractChapters = false
            }, probeToken).ConfigureAwait(false);
            source.MediaStreams = info.MediaStreams;
            source.SupportsDirectPlay = info.MediaStreams.Any(s => s.Type == MediaStreamType.Video) && !string.IsNullOrWhiteSpace(info.Container);
            source.Container = info.Container ?? source.Container;
            source.Formats = info.Formats;
            source.Bitrate = info.Bitrate;
            source.RunTimeTicks ??= info.RunTimeTicks;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "3Cat: no s'ha pogut analitzar una font del vídeo {Video}.", video);
        }
        finally { if (acquired) ProbeGate.Release(); }
        cancellationToken.ThrowIfCancellationRequested();
        var streams = source.MediaStreams.ToList();
        var nextIndex = streams.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
        foreach (var track in subtitles)
        {
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle, Codec = "webvtt", Title = track.Name,
                Language = track.Language, Index = nextIndex++, Path = track.Path,
                IsExternal = true, IsExternalUrl = false, SupportsExternalStream = true
            });
        }
        source.MediaStreams = streams;
        return source;
    }

    private async Task<IReadOnlyList<CachedSubtitle>> PrepareSubtitlesAsync(string video, ThreeCatMedia? media, CancellationToken ct)
    {
        var prepared = new List<CachedSubtitle>();
        if (Plugin.Instance?.Configuration.WriteSubtitles != true || media?.Subtitles is not { Count: > 0 } tracks) return prepared;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks.Take(32))
        {
            var language = (track.Iso ?? "und").Split('-')[0].Trim().ToLowerInvariant();
            if (!Uri.TryCreate(track.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || language.Length is < 1 or > 8 || !language.All(char.IsAsciiLetterOrDigit) || !seen.Add(uri.AbsoluteUri)) continue;
            try
            {
                var directory = Path.Combine(_paths.CachePath, "3cat-dynamic-subtitles");
                Directory.CreateDirectory(directory);
                var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16].ToLowerInvariant();
                var filename = Path.Combine(directory, $"{video}.{language}.{key}.vtt");
                if (!File.Exists(filename) || File.GetLastWriteTimeUtc(filename) < DateTime.UtcNow.AddDays(-1))
                {
                    var vtt = await _client.GetStringAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(vtt) || !vtt.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("WEBVTT", StringComparison.Ordinal)
                        || vtt.Length > 4_000_000) continue;
                    var temp = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllTextAsync(temp, vtt, ct).ConfigureAwait(false);
                        File.Move(temp, filename, overwrite: true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                prepared.Add(new CachedSubtitle(language, string.IsNullOrWhiteSpace(track.Name) ? language : track.Name.Trim(), filename));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "3Cat: no s'ha pogut preparar un subtítol del vídeo {Video}.", video);
            }
        }
        return prepared;
    }

    private sealed record CachedSubtitle(string Language, string Name, string Path);

    private sealed record Snapshot<T>(DateTimeOffset Expires, IReadOnlyList<T> Items,
        Dictionary<string, int>? Missing = null, string? Signature = null, DateTimeOffset RetryAfter = default, DateTimeOffset LastFull = default)
    {
        public bool Fresh => DateTimeOffset.UtcNow < Expires;
    }

    private sealed record EpisodeEntry(string Id, int Season, int Number, int Part, string Title,
        DateTimeOffset? Aired, DateTimeOffset? Expires, TimeSpan? Duration, string? Image, bool Duplicate, string? Overview, bool ExplicitChild = false, string? Rating = null);
}

/// <summary>The main native channel also participates in Jellyfin's latest-media discovery.</summary>
public sealed class ThreeCatDynamicChannel : ThreeCatChannelBase, ISupportsLatestMedia
{
    public ThreeCatDynamicChannel(ThreeCatClient client, IMediaEncoder encoder, IApplicationPaths paths,
        ILogger<ThreeCatDynamicChannel> logger, ILibraryManager library, IUserManager users)
        : base(client, encoder, paths, logger, library, users) { }
}
