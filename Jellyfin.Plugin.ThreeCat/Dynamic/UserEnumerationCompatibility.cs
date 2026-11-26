namespace Jellyfin.Plugin.ThreeCat.Dynamic;

/// <summary>Jellyfin 10.11 exposed Users as a property; Jellyfin 12 exposes GetUsers().</summary>
internal static class UserEnumerationCompatibility
{
    internal static IEnumerable<T> Read<T>(Type contract, object instance)
    {
        var method = contract.GetMethod("GetUsers", Type.EmptyTypes);
        var property = contract.GetProperty("Users");
        var result = method is not null ? method.Invoke(instance, null) : property?.GetValue(instance);
        return result as IEnumerable<T> ?? throw new NotSupportedException("Unsupported Jellyfin user enumeration API.");
    }
}
