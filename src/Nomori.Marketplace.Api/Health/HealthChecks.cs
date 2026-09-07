using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nomori.Marketplace.Api.Health;

public static class HealthChecks
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    public static IHealthChecksBuilder AddNomoriHealthChecks(this IServiceCollection services)
    {
        return services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag, ReadyTag]);
    }
}
