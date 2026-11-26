using Jellyfin.Plugin.ThreeCat.Dynamic;
using Xunit;

namespace Jellyfin.Plugin.ThreeCat.Tests;

public sealed class UserEnumerationCompatibilityTests
{
    [Fact]
    public void ReadsLegacyProperty()
        => Assert.Equal(new[] { "a" }, UserEnumerationCompatibility.Read<string>(typeof(ILegacy), new Legacy()));

    [Fact]
    public void ReadsNewMethod()
        => Assert.Equal(new[] { "b" }, UserEnumerationCompatibility.Read<string>(typeof(IModern), new Modern()));

    [Fact]
    public void UnknownContractFailsExplicitly()
        => Assert.Throws<NotSupportedException>(() => UserEnumerationCompatibility.Read<string>(typeof(object), new object()));

    public interface ILegacy { IEnumerable<string> Users { get; } }
    public interface IModern { IEnumerable<string> GetUsers(); }
    private sealed class Legacy : ILegacy { public IEnumerable<string> Users => ["a"]; }
    private sealed class Modern : IModern { public IEnumerable<string> GetUsers() => ["b"]; }
}
