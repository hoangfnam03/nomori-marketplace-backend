using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Services.ApplicationInfo;

namespace Nomori.Marketplace.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController(IApplicationInfoService applicationInfoService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(HealthResponse), StatusCodes.Status200OK)]
    public ActionResult<HealthResponse> Get()
    {
        var applicationInfo = applicationInfoService.GetApplicationInfo();
        return Ok(new HealthResponse("Healthy", applicationInfo.ApplicationName, applicationInfo.Version));
    }
}

public sealed record HealthResponse(string Status, string ApplicationName, string Version);
