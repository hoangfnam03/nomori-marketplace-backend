using Nomori.Marketplace.Services.ApplicationInfo;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ApplicationInfoServiceTests
{
    [Fact]
    public void ReturnsFoundationApplicationInfo()
    {
        var service = new ApplicationInfoService();

        var result = service.GetApplicationInfo();

        Assert.Equal("Nomori Marketplace", result.ApplicationName);
        Assert.Equal("foundation", result.Version);
    }
}