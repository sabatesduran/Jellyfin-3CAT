using System.Globalization;

namespace Jellyfin.Plugin.ThreeCat.Dynamic;

internal static class DynamicIds
{
    internal static bool Valid(string? id) => id is { Length: > 0 and <= 32 } && id.All(char.IsAsciiDigit);
    internal static string Program(string id) => $"p:{id}";
    internal static string Season(string id, int season) => $"s:{id}:{season.ToString(CultureInfo.InvariantCulture)}";
    internal static string Video(string program, string id) => $"v:{program}:{id}";
    internal static bool TryProgram(string? value, out string id)
    {
        id = value?.StartsWith("p:", StringComparison.Ordinal) == true ? value[2..] : string.Empty;
        return Valid(id);
    }
    internal static bool TryVideo(string? value, out string id)
    {
        id = string.Empty;
        var parts = value?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != "v" || !Valid(parts[1]) || !Valid(parts[2])) return false;
        id = parts[2]; return true;
    }
    internal static bool TrySeason(string? value, out string id, out int season)
    {
        id = string.Empty; season = -1;
        var parts = value?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != "s" || !Valid(parts[1]) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out season) || season < 0) return false;
        id = parts[1]; return true;
    }
}
