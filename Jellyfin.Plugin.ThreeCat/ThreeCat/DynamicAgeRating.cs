using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThreeCat.ThreeCat;

/// <summary>Only explicit published ratings; never infer a rating from the SX3 category.</summary>
internal static class DynamicAgeRating
{
    internal static string? FromLabels(IEnumerable<ThreeCatLabel>? labels)
    {
        var values = (labels ?? []).Select(l => FromCode(l.Id) ?? FromDescription(l.Description))
            .Where(v => v is not null).Select(v => int.Parse(v!, CultureInfo.InvariantCulture)).ToArray();
        return values.Length == 0 ? null : values.Max().ToString(CultureInfo.InvariantCulture);
    }

    internal static string? FromCode(string? code)
    {
        if (string.Equals(code, "CE_TP", StringComparison.OrdinalIgnoreCase)) return "0";
        if (code?.StartsWith("CE_", StringComparison.OrdinalIgnoreCase) != true) return null;
        return ParseAge(code[3..]);
    }

    internal static string? FromDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var s = description.Trim();
        if (s.Equals("Tots els públics", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("TP", StringComparison.OrdinalIgnoreCase)) return "0";
        var match = Regex.Match(s, @"^\+?(\d{1,2})(?:\+|\s+anys)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? ParseAge(match.Groups[1].Value) : null;
    }

    private static string? ParseAge(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && n is >= 0 and <= 18 ? n.ToString(CultureInfo.InvariantCulture) : null;
}
