using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Bounded, atomic JSON snapshots. These contain catalogue metadata, never credentials or video files.</summary>
internal static class DynamicSnapshotStore
{
    private const long MaxBytes = 32_000_000;
    internal static T? Read<T>(string path, ILogger logger) where T : class
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "3Cat: no s'ha pogut llegir una còpia del catàleg.");
            return null;
        }
    }

    internal static void Write<T>(string path, T snapshot, ILogger logger, int? maxFiles = null)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, snapshot);
                if (stream.Length > MaxBytes) return;
            }
            File.Move(temp, path, overwrite: true);
            if (maxFiles is { } limit)
            {
                foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Skip(limit))
                    old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "3Cat: no s'ha pogut desar una còpia del catàleg.");
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
