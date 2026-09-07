using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Controllers;
using Nomori.Marketplace.Services.ApplicationInfo;

namespace Nomori.Marketplace.Api.Tests;

public sealed class HealthControllerTests
{
    [Fact]
    public void ReturnsHealthyResponse()
    {
        var controller = new HealthController(new ApplicationInfoService());

        var result = controller.Get();

        var response = Assert.IsType<OkObjectResult>(result.Result);
        var health = Assert.IsType<HealthResponse>(response.Value);
        Assert.Equal("Healthy", health.Status);
    }
}
