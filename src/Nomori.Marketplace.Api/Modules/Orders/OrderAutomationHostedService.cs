using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Api.Modules.Orders;

/// <summary>
/// Runs <see cref="IOrderAutomation"/> every <see cref="OrderOptions.AutomationIntervalMinutes"/> minutes until background jobs
/// exist (F29). A failed run is logged and the next one tries again; every step is safe to repeat.
/// </summary>
public sealed partial class OrderAutomationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderOptions> options,
    ILogger<OrderAutomationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutomationEnabled) return;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.AutomationIntervalMinutes));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IOrderAutomation>().RunAsync(stoppingToken);
                if (result.Cancelled + result.Delivered + result.Completed > 0)
                    LogRun(result.Cancelled, result.Delivered, result.Completed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order automation: {Cancelled} cancelled, {Delivered} delivered, {Completed} completed.")]
    private partial void LogRun(int cancelled, int delivered, int completed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Order automation run failed.")]
    private partial void LogFailed(Exception exception);
}
