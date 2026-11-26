using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Local 16:9 channel artwork, available without external requests.</summary>
internal static class ThreeCatChannelArtwork
{
    // 3Cat artwork supplied by the user, resized and optimized for the channel tile.
    private static readonly byte[] Png = Convert.FromBase64String("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");

    internal static IEnumerable<ImageType> Supported => [ImageType.Primary, ImageType.Thumb];

    internal static DynamicImageResponse Get(ImageType type)
        => type is ImageType.Primary or ImageType.Thumb
            ? new DynamicImageResponse
            {
                HasImage = true,
                Format = ImageFormat.Png,
                Stream = new MemoryStream(Png, writable: false)
            }
            : new DynamicImageResponse { HasImage = false };
}
