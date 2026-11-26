using Jellyfin.Plugin.ThreeCat.ThreeCat;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Separate browsing channel for programmes tagged SX3; not a parental-control guarantee.</summary>
public sealed class ThreeCatSx3Channel : ThreeCatDynamicChannel
{
    public ThreeCatSx3Channel(ThreeCatClient client, IMediaEncoder encoder, IApplicationPaths paths, ILogger<ThreeCatDynamicChannel> logger)
        : base(client, encoder, paths, logger) { }

    protected override bool ChildrenOnly => true;
    public override string Name => "SX3 a la carta";
    public override string Description => "Programes identificats per 3Cat com a SX3. Revisa els permisos i l'edat del contingut.";
    public override bool IsEnabledFor(string userId) => base.IsEnabledFor(userId) && Plugin.Instance?.Configuration.DynamicSx3 == true;
    public override IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public override Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });
}
