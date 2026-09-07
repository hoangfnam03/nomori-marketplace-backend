namespace Nomori.Marketplace.Services.ApplicationInfo;

public sealed class ApplicationInfoService : IApplicationInfoService
{
    public ApplicationInfoResponse GetApplicationInfo()
    {
        return new ApplicationInfoResponse("Nomori Marketplace", "foundation");
    }
}
