using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Integration;

/// <summary>
/// Keeps the website's copy in sync every few minutes. Waits until the CMS schema and seed content
/// are ready, runs once straight away, then on a fixed interval. A failed run is logged and the loop
/// carries on.
/// </summary>
public sealed class SyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    ISiteReadiness readiness,
    IOptions<IntegrationOptions> options,
    TimeProvider clock,
    ILogger<SyncBackgroundService> logger) : BackgroundService
{
    internal const string ScheduledTrigger = "scheduled";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await readiness.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.SyncIntervalMinutes));
        logger.LogInformation("Background course sync started; running every {Interval}", interval);

        using var timer = new PeriodicTimer(interval, clock);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<ICourseSyncService>();
            await sync.RunAsync(ScheduledTrigger, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled course sync threw; will retry on the next interval");
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
