using System.Globalization;
using System.Net;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Events;
using Chelmsford.Core.Payments;
using Chelmsford.Web.Models;
using Chelmsford.Web.Services;
using Chelmsford.Web.Umbraco;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Web.Common.Controllers;
using Umbraco.Extensions;
using static Chelmsford.Web.Umbraco.DocTypes;
using SourceText = Chelmsford.Core.Common.SourceText;

namespace Chelmsford.Web.Controllers;

/// <summary>One template for every course. Facts come from the synced copy; marketing extras from the CMS node.</summary>
public sealed class CourseController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor,
    SiteContent content,
    ICourseSearchService courses,
    IPayableItemCatalogue payableItems)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    private const string ExampleContact = "courses@example.ac.uk";

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var page = CurrentPage!;
        if (page.Int(Props.CourseId) is not { } courseId || await courses.GetAsync(courseId, cancellationToken) is not { } course)
        {
            return NotFoundPage();
        }

        var now = UkTime.NowLocal;
        var extraItems = (await payableItems.GetAllAsync(cancellationToken))
            .Where(i => i.CourseIds.Contains(course.Id) && !PayableItemRules.IsClosed(i, now))
            .OrderBy(i => i.Type)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var additionalSummary = TextHelpers.Truncate(SourceText.PlainText(course.AdditionalFeesHtml), 140);
        var additionalCosts = new List<string>();
        if (additionalSummary.Length > 0)
        {
            additionalCosts.Add(additionalSummary);
        }

        additionalCosts.AddRange(extraItems.Select(i => $"{i.TypeName}: {i.Name}, {UkTime.Money(i.AmountPence)}"));

        var fundingLabel = course.FundingGroups.Count > 0 ? string.Join(", ", course.FundingGroups) : "Contact us about funding";

        var sections = new List<CourseSection>();
        void Add(string id, string title, string shortTitle, params string?[] html)
        {
            var joined = string.Concat(html.Where(h => !string.IsNullOrWhiteSpace(h)));
            if (SourceText.PlainText(joined).Length > 0)
            {
                sections.Add(new CourseSection(id, title, shortTitle, joined));
            }
        }

        Add("overview", "Overview", "Overview", course.OverviewHtml);
        Add("study", "What you'll study", "What you'll study", course.StudyHtml);
        Add("assessment", "How you'll be assessed", "Assessment", course.AssessmentHtml);
        Add("entry", "Entry requirements", "Entry requirements", course.EntryHtml);
        Add("fees", "Fees, funding and additional costs", "Fees and funding",
            "<h3>Course fee</h3>",
            course.FeesHtml ?? (course.FeesText is { } ft ? $"<p>{WebUtility.HtmlEncode(ft)}</p>" : "<p>Contact us for the fee.</p>"),
            "<h3>Funding</h3>",
            course.FundingGroups.Count > 0
                ? "<ul>" + string.Concat(course.FundingGroups.Select(g => $"<li>{WebUtility.HtmlEncode(g)}</li>")) + "</ul>"
                : "<p>Contact us about funding.</p>",
            "<h3>Additional costs</h3>",
            course.AdditionalFeesHtml,
            extraItems.Count > 0
                ? "<ul>" + string.Concat(extraItems.Select(i => $"<li>{WebUtility.HtmlEncode(i.TypeName)}: {WebUtility.HtmlEncode(i.Name)}, {WebUtility.HtmlEncode(UkTime.Money(i.AmountPence))}</li>")) + "</ul>"
                : null,
            string.IsNullOrWhiteSpace(course.AdditionalFeesHtml) && extraItems.Count == 0 ? "<p>None listed.</p>" : null,
            "<p>Materials fees, trips and deposits can be <a href=\"/make-a-payment/\">paid online</a>.</p>");
        Add("progression", "Where it can lead", "Where it can lead", course.ProgressionHtml);

        var picture = page.Media(Props.HeroImage) is { } hero
            ? Picture.FromUrl(hero.Url, hero.Alt)
            : Picture.FromSlug(SiteAssets.SlugForSubject(course.Subject), "");

        var upcoming = content.UpcomingEvents();
        var nextEvent = upcoming.FirstOrDefault(e => e.Type == EventTypes.OpenEvents) ?? upcoming.FirstOrDefault();
        var contact = course.ContactEmail ?? ExampleContact;

        var model = new CourseViewModel
        {
            Course = course,
            Title = (course.SeoTitle ?? course.Heading) + " | Chelmsford College",
            MetaDescription = course.MetaDescription ?? TextHelpers.FirstSentence(course.OverviewHtml, 160),
            Section = "courses",
            HasStickyBar = true,
            Intro = TextHelpers.FirstSentence(course.OverviewHtml),
            Picture = picture,
            EntrySummary = TextHelpers.Truncate(SourceText.PlainText(course.EntryHtml), 140),
            FeeLabel = string.IsNullOrWhiteSpace(course.FeesText) ? "Contact us" : TextHelpers.Truncate(course.FeesText, 60),
            FundingLabel = fundingLabel,
            AdditionalCosts = additionalCosts,
            Sections = sections,
            StudentQuote = page.Text(Props.StudentQuote),
            StudentName = page.Text(Props.StudentName),
            VideoEmbedUrl = TextHelpers.VideoEmbedUrl(page.Text(Props.VideoUrl)),
            CampaignMessage = page.Text(Props.CampaignMessage),
            NextEvent = nextEvent,
            CoursesUrl = page.Parent()?.Url() ?? "/courses/",
            ApplyUrl = "/apply/?course=" + course.Id.ToString(CultureInfo.InvariantCulture),
            EnquireUrl = "mailto:" + contact + "?subject=" + Uri.EscapeDataString("Enquiry: " + course.Heading)
        };

        var origin = $"{Request.Scheme}://{Request.Host}";
        model.JsonLd = TextHelpers.JsonLd(BuildJsonLd(course, model, origin, origin + page.Url()));
        return Page("Course", model);
    }

    /// <summary>schema.org Course with an offer (free or paid), a schedule when the length can be read, and an on-site instance.</summary>
    private static Dictionary<string, object?> BuildJsonLd(SyncedCourse course, CourseViewModel model, string origin, string url)
    {
        var free = course.FundingGroups.Contains(CourseVocabulary.Funding16To18);
        var offer = new Dictionary<string, object?>
        {
            ["@type"] = "Offer",
            ["category"] = free ? "Free" : "Paid",
            ["url"] = url
        };

        if (free)
        {
            offer["price"] = 0;
            offer["priceCurrency"] = "GBP";
        }
        else if (TextHelpers.PlainPoundAmount(course.FeesText) is { } price)
        {
            offer["price"] = price;
            offer["priceCurrency"] = "GBP";
        }

        var instance = new Dictionary<string, object?>
        {
            ["@type"] = "CourseInstance",
            ["courseMode"] = "Onsite",
            ["startDate"] = course.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["location"] = new Dictionary<string, object?>
            {
                ["@type"] = "Place",
                ["name"] = course.Campus + " campus, Chelmsford College",
                ["address"] = course.Campus + ", Chelmsford, Essex"
            }
        };

        if (TextHelpers.IsoDuration(course.Duration) is { } duration)
        {
            instance["courseSchedule"] = new Dictionary<string, object?>
            {
                ["@type"] = "Schedule",
                ["duration"] = duration,
                ["repeatCount"] = 1
            };
        }

        return new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Course",
            ["name"] = course.Heading,
            ["description"] = model.MetaDescription,
            ["url"] = url,
            ["courseCode"] = course.CourseCode,
            ["educationalLevel"] = course.LevelLong,
            ["provider"] = new Dictionary<string, object?>
            {
                ["@type"] = "CollegeOrUniversity",
                ["name"] = "Chelmsford College",
                ["url"] = origin + "/"
            },
            ["offers"] = offer,
            ["hasCourseInstance"] = instance
        };
    }
}
