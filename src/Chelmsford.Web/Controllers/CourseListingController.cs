using Chelmsford.Core.Courses;
using Chelmsford.Web.Models;
using Chelmsford.Web.Services;
using Chelmsford.Web.Umbraco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Extensions;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Controllers;

/// <summary>Course search at /courses/. A plain GET form, so filters work without JavaScript.</summary>
public sealed class CourseListingController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor,
    SiteContent content,
    ICourseSearchService courses)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var page = CurrentPage!;
        var q = Request.Query;

        static IReadOnlyCollection<string> Values(Microsoft.Extensions.Primitives.StringValues values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();

        var query = new CourseSearchQuery
        {
            Text = q["q"].ToString() is { Length: > 0 } text ? text.Trim()[..Math.Min(text.Trim().Length, 100)] : null,
            Subjects = Values(q["subject"]),
            Levels = Values(q["level"]),
            Modes = Values(q["mode"]),
            Campuses = Values(q["campus"]),
            Funding = Values(q["funding"])
        };

        // Job words such as "chef" are expanded to the words courses use ("catering") in the web layer.
        var result = await courses.SearchWithSynonymsAsync(query, cancellationToken);
        var urls = content.CourseUrls();
        var model = new CourseListingViewModel
        {
            Title = query.Text is null && query.ActiveFilterCount == 0
                ? "Find a course | Chelmsford College"
                : $"Course search results ({result.Courses.Count}) | Chelmsford College",
            MetaDescription = "Search courses at Chelmsford College by subject, level, study mode and campus.",
            Section = "courses",
            Intro = page.Text(Props.Intro),
            Query = query,
            Result = result,
            ListingUrl = page.Url(),
            Cards = result.Courses.Select(c => new CourseCard { Course = c, Url = urls.TryGetValue(c.Id, out var url) ? url : page.Url() }).ToList()
        };

        return Page("CourseListing", model);
    }
}
