using Chelmsford.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;

namespace Chelmsford.Web.Controllers;

public sealed class HomeController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor,
    SiteContent content,
    HomePageBuilder builder)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = await builder.BuildAsync(CurrentPage!, content.LiveCampaign(), $"{Request.Scheme}://{Request.Host}", cancellationToken);
        return Page("Home", model);
    }
}
