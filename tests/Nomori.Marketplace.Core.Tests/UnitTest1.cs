namespace Nomori.Marketplace.Core.Tests;

public sealed class FoundationTests
{
    [Fact]
    public void CoreAssemblyIsAvailable()
    {
        Assert.NotNull(typeof(Nomori.Marketplace.Core.AssemblyMarker).Assembly);
    }
}
