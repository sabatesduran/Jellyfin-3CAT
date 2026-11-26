namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Reconcile only successful, complete observations. A missing item is not immediately deleted.</summary>
internal static class DynamicReconciliation
{
    internal static (IReadOnlyList<T> Items, Dictionary<string, int> Missing) Merge<T>(
        IEnumerable<T> previous, IEnumerable<T> observed, IReadOnlyDictionary<string, int>? missing,
        Func<T, string> key, int confirmations = 3)
    {
        if (confirmations < 2) throw new ArgumentOutOfRangeException(nameof(confirmations));
        var result = observed.DistinctBy(key, StringComparer.Ordinal).ToDictionary(key, StringComparer.Ordinal);
        var next = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var old in previous)
        {
            var id = key(old);
            if (result.ContainsKey(id)) continue;
            var count = missing is not null && missing.TryGetValue(id, out var value) ? value + 1 : 1;
            if (count >= confirmations) continue;
            result[id] = old;
            next[id] = count;
        }
        return (result.Values.ToArray(), next);
    }
}
