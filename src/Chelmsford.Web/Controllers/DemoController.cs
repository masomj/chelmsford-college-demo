using System.Globalization;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Web.Models;
using Chelmsford.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Chelmsford.Web.Controllers;

/// <summary>"How the course data gets here": the integration layer explained for the tender panel.</summary>
public sealed class DemoController(
    ICourseSyncService sync,
    ICollegeSourceReader sourceReader,
    ICourseSearchService courses,
    SiteContent content,
    ILogger<DemoController> logger) : Controller
{
    [HttpGet("/how-the-data-gets-here")]
    public async Task<IActionResult> Index([FromQuery(Name = "course")] int? courseId, [FromQuery] string? synced, CancellationToken cancellationToken)
    {
        var model = new DataFlowViewModel
        {
            Title = "How the course data gets here | Chelmsford College",
            MetaDescription = "How the website reads the college's course tables, cleans the data and keeps its own copy in sync.",
            LastRun = await sync.LastRunAsync(cancellationToken),
            JustSynced = synced == "1"
        };

        SourceSnapshot? snapshot = null;
        try
        {
            snapshot = await sourceReader.ReadAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the college source for the data page");
            model.SourceError = "The college's tables could not be read just now. The website keeps serving its last synced copy.";
        }

        if (snapshot is not null)
        {
            model.CourseOptions = snapshot.Courses
                .Select(c => (Id: Core.Common.SourceText.ParseInt(c["C_ID"]) ?? 0, Title: Core.Common.SourceText.PlainText(Core.Common.SourceText.Clean(c["C_TITLE"]))))
                .Where(c => c.Id > 0)
                .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // With no course chosen, start on the first course whose raw text shows the cleaning at work.
            var firstMessy = model.CourseOptions
                .Where(o => snapshot.Courses.FirstOrDefault(c => Core.Common.SourceText.ParseInt(c["C_ID"]) == o.Id) is { } row
                            && (row["C_STUDYOVERVIEW"] ?? string.Empty) is var text
                            && (text.Contains("glyphicon", StringComparison.OrdinalIgnoreCase) || text.Contains("_x000D_", StringComparison.Ordinal)))
                .Select(o => o.Id)
                .FirstOrDefault();
            var id = courseId is { } requested && model.CourseOptions.Any(c => c.Id == requested) ? requested
                : firstMessy != 0 ? firstMessy
                : model.CourseOptions.FirstOrDefault().Id;
            model.SelectedCourseId = id;

            var raw = snapshot.Courses.FirstOrDefault(c => Core.Common.SourceText.ParseInt(c["C_ID"]) == id);
            var cleaned = await courses.GetAsync(id, cancellationToken);
            model.Cleaned = cleaned;
            if (cleaned is not null && content.CourseUrls().TryGetValue(id, out var url))
            {
                model.CourseUrl = url;
            }

            if (raw is not null)
            {
                model.Fields =
                [
                    new RawField("C_TITLE", raw["C_TITLE"], "Course heading", cleaned?.Heading, false),
                    new RawField("C_FEES", raw["C_FEES"], "Course fee", cleaned?.FeesText ?? "Contact us (no fee in the data)", false),
                    new RawField("C_CONTACT", raw["C_CONTACT"], "Contact email", cleaned?.ContactEmail, false),
                    new RawField("C_DAYS", raw["C_DAYS"], "Days and times", cleaned?.DaysText ?? "Not shown (empty in the data)", false),
                    new RawField("C_STUDYOVERVIEW", raw["C_STUDYOVERVIEW"], "Overview (cleaned HTML)", cleaned?.OverviewHtml, true)
                ];
            }
        }

        return View("~/Views/Demo/DataFlow.cshtml", model);
    }

    [HttpPost("/how-the-data-gets-here/sync")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow([FromForm] int? course, CancellationToken cancellationToken)
    {
        await sync.RunAsync("manual", cancellationToken);
        var query = "?synced=1" + (course is { } c ? "&course=" + c.ToString(CultureInfo.InvariantCulture) : string.Empty);
        return Redirect("/how-the-data-gets-here" + query + "#sync");
    }
}
