using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;

namespace Chelmsford.Web.Umbraco;

/// <summary>
/// On start-up: make sure the schema exists, seed the demo content on first run, run the first sync
/// (which creates the course pages), seed payable items, then let the background sync begin.
/// Each step is isolated so a failure is logged and the site still starts.
/// </summary>
public sealed class StartupHandler(
    IRuntimeState runtimeState,
    IServiceProvider services,
    ISiteReadiness readiness,
    ILogger<StartupHandler> logger) : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != global::Umbraco.Cms.Core.RuntimeLevel.Run)
        {
            logger.LogInformation("Umbraco is at runtime level {Level}; skipping schema and seeding", runtimeState.Level);
            return;
        }

        try
        {
            await StepAsync("site databases", () => services.InitialiseChelmsfordDataAsync(cancellationToken));

            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;

            await StepAsync("CMS schema", () => sp.GetRequiredService<SchemaInstaller>().InstallAsync());

            var seeder = sp.GetRequiredService<ContentSeeder>();
            await StepAsync("demo content", () =>
            {
                if (!seeder.HasAnyContent())
                {
                    seeder.SeedBaseContent();
                }

                return Task.CompletedTask;
            });

            await StepAsync("first course sync", () => sp.GetRequiredService<ICourseSyncService>().RunAsync("startup", cancellationToken));
            await StepAsync("demo page text", () =>
            {
                seeder.RefreshDemoPages();
                return Task.CompletedTask;
            });
            await StepAsync("panel CMS user", () => sp.GetRequiredService<PanelUserProvisioner>().EnsureAsync());
            await StepAsync("payable items", () =>
            {
                seeder.SeedPayableItems();
                return Task.CompletedTask;
            });
        }
        finally
        {
            readiness.MarkReady();
        }
    }

    private async Task StepAsync(string name, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Start-up step '{Step}' failed; the site will start without it", name);
        }
    }
}
