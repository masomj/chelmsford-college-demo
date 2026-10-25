using Chelmsford.Core.Integration;
using Chelmsford.Core.Payments;
using Chelmsford.Web.Configuration;
using Chelmsford.Web.Services;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace Chelmsford.Web.Umbraco;

public sealed class ChelmsfordComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddSingleton<SiteContent>();
        builder.Services.AddSingleton<SiteAssets>();
        builder.Services.AddSingleton<IPayableItemCatalogue, UmbracoPayableItemCatalogue>();
        builder.Services.AddScoped<ICourseSyncObserver, CourseNodeSynchroniser>();
        builder.Services.AddScoped<SchemaInstaller>();
        builder.Services.AddScoped<ContentSeeder>();
        builder.Services.AddSingleton<PayContextProtector>();
        builder.Services.AddScoped<HomePageBuilder>();

        // The panel's CMS login, the access reports and the page-view / CMS sign-in tracking.
        builder.Services.AddOptions<PanelOptions>().Bind(builder.Config.GetSection(PanelOptions.SectionName));
        builder.Services.AddOptions<ReportsOptions>().Bind(builder.Config.GetSection(ReportsOptions.SectionName));
        builder.Services.AddScoped<PanelUserProvisioner>();
        builder.Services.AddSingleton<ReportsAuthenticator>();
        builder.Services.AddSingleton<PageViewQueue>();
        builder.Services.AddHostedService<PageViewRecorder>();
        builder.Services.AddSingleton<CmsAccessRecorder>();

        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, StartupHandler>();
        builder.AddNotificationAsyncHandler<UserLoginSuccessNotification, CmsLoginHandler>();
        builder.AddNotificationAsyncHandler<UserLoginFailedNotification, CmsLoginFailedHandler>();
        builder.AddNotificationAsyncHandler<UserLogoutSuccessNotification, CmsLogoutHandler>();
        builder.SetContentLastChanceFinder<NotFoundContentFinder>();
    }
}

/// <summary>Serves the friendly "Page not found" content page, with a 404 status, for unknown URLs.</summary>
public sealed class NotFoundContentFinder(
    IUmbracoContextAccessor umbracoContextAccessor,
    IDocumentNavigationQueryService navigation) : IContentLastChanceFinder
{
    public Task<bool> TryFindContent(IPublishedRequestBuilder request)
    {
        if (!umbracoContextAccessor.TryGetUmbracoContext(out var context) || context.Content is null)
        {
            return Task.FromResult(false);
        }

        if (!navigation.TryGetRootKeysOfType(DocTypes.Home, out var homes))
        {
            return Task.FromResult(false);
        }

        var page = homes
            .Select(k => context.Content.GetById(k))
            .OfType<IPublishedContent>()
            .SelectMany(h => h.Children() ?? [])
            .FirstOrDefault(c => c.ContentType.Alias == DocTypes.ContentPage && c.Name == ContentSeeder.NotFoundPageName);

        if (page is null)
        {
            return Task.FromResult(false);
        }

        request.SetPublishedContent(page);
        request.SetIs404();
        return Task.FromResult(true);
    }
}
