namespace Jellyfin.Plugin.ThreeCat.Dynamic;

internal sealed record DynamicStatusSnapshot(DateTimeOffset? LastSuccess, int Programmes, int Films, int Sx3, string State, string? Error);

internal static class DynamicStatus
{
    private static DynamicStatusSnapshot _current = new(null, 0, 0, 0, "not_started", null);
    internal static DynamicStatusSnapshot Current => Volatile.Read(ref _current);
    internal static void Success(int programmes, int films, int sx3)
        => Volatile.Write(ref _current, new(DateTimeOffset.UtcNow, programmes, films, sx3, "ready", null));
    internal static void Restore(DateTimeOffset lastSuccess, int programmes, int films, int sx3)
        => Volatile.Write(ref _current, new(lastSuccess, programmes, films, sx3, "cached", null));
    internal static void Failure(string error)
    {
        var current = Current;
        Volatile.Write(ref _current, current with { State = "degraded", Error = error });
    }
}
