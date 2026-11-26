using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Local 16:9 channel artwork, available without external requests.</summary>
internal static class ThreeCatChannelArtwork
{
    // Original text-only tile for this unofficial integration; not the official corporate logo.
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
