using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Indexes the root on startup and hourly, without fetching all episodes.</summary>
public sealed class ThreeCatDynamicIndexTask : IScheduledTask
{
    private readonly IChannelManager _manager;
    public ThreeCatDynamicIndexTask(IChannelManager manager) => _manager = manager;
    public string Name => "Actualitza el catàleg virtual de 3Cat";
    public string Key => "ThreeCatDynamicRootIndex";
    public string Description => "Indexa programes nous sense carregar-ne els episodis ni descarregar vídeos.";
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
        }, progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }
    ];
}
