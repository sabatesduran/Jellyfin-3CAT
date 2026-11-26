using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Indexes the catalogue and refreshes the union of native per-user favourite programmes.</summary>
public sealed class ThreeCatDynamicIndexTask : IScheduledTask
{
    private readonly IChannelManager _manager;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<ThreeCatDynamicIndexTask> _logger;

    public ThreeCatDynamicIndexTask(IChannelManager manager, ILibraryManager library, IUserManager users, ILogger<ThreeCatDynamicIndexTask> logger)
    {
        _manager = manager;
        _library = library;
        _users = users;
        _logger = logger;
    }

    public string Name => "Actualitza el catàleg virtual de 3Cat";
    public string Key => "ThreeCatDynamicRootIndex";
    public string Description => "Indexa programes nous i actualitza els episodis dels programes preferits.";
    public string Category => "3Cat";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.DynamicCatalogue != true)
        {
            progress.Report(100);
            return;
        }
        var channels = await _manager.GetChannelsInternalAsync(new ChannelQuery()).ConfigureAwait(false);
        var channel = channels.Items.FirstOrDefault(c => c.Name == "3Cat a la carta");
        if (channel is null) throw new InvalidOperationException("Canal 3Cat no registrat. Reinicia Jellyfin.");
        await _manager.GetChannelItemsInternal(new InternalItemsQuery
        {
            ChannelIds = [channel.Id],
            EnableTotalRecordCount = false
        }, new Progress<double>(), cancellationToken).ConfigureAwait(false);
        progress.Report(30);

        try
        {
            await RefreshFavouritesAsync(channel, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "3Cat: catàleg indexat, però no s'han pogut actualitzar els preferits.");
        }
        progress.Report(100);
    }

    private async Task RefreshFavouritesAsync(Channel channel, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // Query Jellyfin's user data rather than storing a second (shared) favourites list.
        // A union avoids refreshing the same programme more than once.
        var favourites = new Dictionary<Guid, Series>();
        foreach (var user in UserEnumerationCompatibility.Read<User>(typeof(IUserManager), _users))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!channel.IsVisible(user)) continue;
            var query = new InternalItemsQuery(user)
            {
                Parent = channel,
                IncludeItemTypes = [BaseItemKind.Series],
                IsFavorite = true,
                EnableTotalRecordCount = false
            };
            foreach (var series in _library.GetItemList(query, true).OfType<Series>())
            {
                if (series.ChannelId == channel.Id) favourites.TryAdd(series.Id, series);
            }
        }

        var completed = 0;
        foreach (var series in favourites.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Use unfiltered child queries when synchronizing: ChannelManager reconciles item deletions.
                var seasons = await _manager.GetChannelItemsInternal(new InternalItemsQuery
                {
                    Parent = series,
                    ChannelIds = [channel.Id],
                    EnableTotalRecordCount = false
                }, new Progress<double>(), cancellationToken).ConfigureAwait(false);
                foreach (var season in seasons.Items.OfType<Season>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _manager.GetChannelItemsInternal(new InternalItemsQuery
                    {
                        Parent = season,
                        ChannelIds = [channel.Id],
                        EnableTotalRecordCount = false
                    }, new Progress<double>(), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "3Cat: no s'ha pogut actualitzar un programa preferit ({Id}).", series.Id);
            }
            progress.Report(30 + 70d * ++completed / favourites.Count);
        }
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }
    ];
}
