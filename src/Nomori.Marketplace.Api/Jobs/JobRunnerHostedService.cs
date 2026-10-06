using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Jobs;

namespace Nomori.Marketplace.Api.Jobs;

/// <summary>
/// Wakes up every few seconds and runs the jobs that are due. It decides nothing itself: which job may run is settled in the database, so
/// several nodes can run this at once. Switched off with <c>Jobs:Enabled=false</c>.
/// </summary>
public sealed partial class JobRunnerHostedService(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<JobRunnerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogSwitchedOff();
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 5, 600));
        using var timer = new PeriodicTimer(delay);
        do
        {
            try
            {
                // Every tick gets its own scope: the jobs use scoped services.
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IJobService>().RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database may be down for a moment. The runner must outlive that.
                LogTickFailed(exception);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Background jobs are switched off on this node (Jobs:Enabled = false).")]
    private partial void LogSwitchedOff();

    [LoggerMessage(Level = LogLevel.Error, Message = "The job runner could not check for due jobs.")]
    private partial void LogTickFailed(Exception exception);

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
