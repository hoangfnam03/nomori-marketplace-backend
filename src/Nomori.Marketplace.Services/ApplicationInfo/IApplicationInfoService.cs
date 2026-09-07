namespace Nomori.Marketplace.Services.ApplicationInfo;

public interface IApplicationInfoService
{
    ApplicationInfoResponse GetApplicationInfo();
}

public sealed record ApplicationInfoResponse(string ApplicationName, string Version);
