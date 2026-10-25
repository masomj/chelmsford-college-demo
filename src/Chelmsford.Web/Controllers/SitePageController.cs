using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;

namespace Chelmsford.Web.Controllers;

/// <summary>
/// Base for the hijacked page controllers (one per document type, named {Alias}Controller). Pages
/// are rendered from explicit views, so no Umbraco templates are needed. Each controller exposes an
/// async Index action in place of the synchronous one.
/// </summary>
public abstract class SitePageController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor)
    : RenderController(logger, compositeViewEngine, umbracoContextAccessor)
{
    [NonAction]
    public sealed override IActionResult Index() => throw new NotSupportedException("Use the async Index action.");

    protected IActionResult Page(string view, object model) => View($"~/Views/Pages/{view}.cshtml", model);

    protected IActionResult NotFoundPage() => SiteResults.NotFoundView(this);
}

public static class SiteResults
{
    public static IActionResult NotFoundView(Controller controller)
    {
        controller.Response.StatusCode = StatusCodes.Status404NotFound;
        return controller.View("~/Views/Pages/NotFound.cshtml", new Models.SimplePageViewModel
        {
            Title = "Page not found",
            Heading = "Page not found",
            Summary = "If you typed the web address, check it is correct. If you pasted it, check you copied the whole address."
        });
    }
}
