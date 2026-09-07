namespace Nomori.Marketplace.Data.Tests;

public sealed class DataFoundationTests
{
    [Fact]
    public void DataTestAssemblyIsAvailable()
    {
        Assert.NotNull(typeof(DataFoundationTests).Assembly);
    }
}