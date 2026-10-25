using System.Globalization;
using Chelmsford.Core.Events;
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

/// <summary>Events at /events/, filtered by type and month with plain GET links and a GET form.</summary>
public sealed class EventListingController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor,
    SiteContent content)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public IActionResult Index(string? type, string? month)
    {
        var page = CurrentPage!;
        var upcoming = content.UpcomingEvents();
        type = EventTypes.All.FirstOrDefault(t => t.Equals(type, StringComparison.OrdinalIgnoreCase));
        var months = upcoming
            .Select(e => new DateTime(e.StartsAt.Year, e.StartsAt.Month, 1))
            .Distinct()
            .OrderBy(d => d)
            .Select(d => (UkTime.MonthKey(d), UkTime.MonthLabel(d)))
            .ToList();
        month = months.Any(m => m.Item1 == month) ? month : null;

        var model = new EventListingViewModel
        {
            Title = "Events | Chelmsford College",
            MetaDescription = page.Text(Props.Intro),
            Section = "events",
            Intro = page.Text(Props.Intro),
            Type = type,
            Month = month,
            Types = EventTypes.All,
            Months = months,
            ListingUrl = page.Url(),
            Events = upcoming
                .Where(e => type is null || e.Type == type)
                .Where(e => month is null || UkTime.MonthKey(e.StartsAt) == month)
                .ToList()
        };

        return Page("EventListing", model);
    }
}

public sealed class EventController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor,
    IEventBookingService bookings)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    /// <summary>TempData key that carries the result of a booking across the redirect back to the event page.</summary>
    public const string BookedKey = "BookingResult";

    public static string BookingResult(Guid eventKey, string result) => $"{result}|{eventKey:D}";

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var page = CurrentPage!;
        if (SiteContent.ToEvent(page) is not { } evt)
        {
            return NotFoundPage();
        }

        var model = await BuildAsync(evt, page.Parent()?.Url() ?? "/events/", bookings, Request.Scheme + "://" + Request.Host, cancellationToken);

        // Read once: after a reload the confirmation is gone and the booking form is back.
        var result = TempData[BookedKey] as string;
        string? outcome = null;
        if (result is not null && result.EndsWith("|" + evt.Key.ToString("D"), StringComparison.Ordinal))
        {
            outcome = result[..result.IndexOf('|', StringComparison.Ordinal)];
        }

        model.Booked = outcome == "booked";
        model.BookingProblem = outcome switch
        {
            "full" => "Sorry, there are not enough places left for that many people. Try booking fewer places, or choose another event.",
            "closed" => "Booking for this event has closed.",
            _ => null
        };

        if (TempData["BookingErrors"] is string errors && TempData["BookingForm"] is string form)
        {
            model.Errors = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(errors) ?? model.Errors;
            model.Form = System.Text.Json.JsonSerializer.Deserialize<BookingForm>(form) ?? model.Form;
        }

        model.Form.EventKey = evt.Key;
        return Page("Event", model);
    }

    public static async Task<EventViewModel> BuildAsync(EventInfo evt, string eventsUrl, IEventBookingService bookings, string origin, CancellationToken cancellationToken)
    {
        int? placesLeft = null;
        if (evt.Capacity is { } capacity)
        {
            placesLeft = Math.Max(0, capacity - await bookings.PlacesTakenAsync(evt.Key, cancellationToken));
        }

        var picture = evt.Image ?? Picture.FromSlug(SiteAssets.SlugForEventType(evt.Type, evt.Name));
        var model = new EventViewModel
        {
            Event = evt,
            Title = $"{evt.Name}, {UkTime.Date(evt.StartsAt)} | Chelmsford College",
            MetaDescription = evt.Summary,
            Section = "events",
            Picture = picture,
            PlacesLeft = placesLeft,
            EventsUrl = eventsUrl,
            IsPast = (evt.EndsAt ?? evt.StartsAt) < UkTime.NowLocal
        };

        string Iso(DateTime local) => new DateTimeOffset(local, UkTime.London.GetUtcOffset(local)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        model.JsonLd = TextHelpers.JsonLd(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Event",
            ["name"] = evt.Name,
            ["description"] = evt.Summary,
            ["startDate"] = Iso(evt.StartsAt),
            ["endDate"] = evt.EndsAt is { } end ? Iso(end) : null,
            ["eventAttendanceMode"] = "https://schema.org/OfflineEventAttendanceMode",
            ["eventStatus"] = "https://schema.org/EventScheduled",
            ["url"] = origin + evt.Url,
            ["location"] = new Dictionary<string, object?>
            {
                ["@type"] = "Place",
                ["name"] = evt.Venue ?? "Chelmsford College",
                ["address"] = (evt.Address ?? "Chelmsford, Essex").Replace("\r", string.Empty).Replace("\n", ", ")
            },
            ["organizer"] = new Dictionary<string, object?> { ["@type"] = "CollegeOrUniversity", ["name"] = "Chelmsford College", ["url"] = origin + "/" },
            ["offers"] = new Dictionary<string, object?>
            {
                ["@type"] = "Offer",
                ["price"] = 0,
                ["priceCurrency"] = "GBP",
                ["availability"] = placesLeft is 0 ? "https://schema.org/SoldOut" : "https://schema.org/InStock",
                ["url"] = origin + evt.Url + "#book"
            }
        });

        return model;
    }
}

public sealed class ContentPageController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public IActionResult Index(CancellationToken cancellationToken)
    {
        var page = CurrentPage!;
        var name = page.Name ?? string.Empty;
        var model = new SimplePageViewModel
        {
            Title = name + " | Chelmsford College",
            Heading = name,
            Summary = page.Text(Props.Summary),
            MetaDescription = page.Text(Props.Summary),
            BodyHtml = page.Html(Props.Body),
            Section = name switch
            {
                "Adult learners" => "adults",
                "Apprenticeships" => "apprenticeships",
                "Employers" => "employers",
                "Parents and carers" => "parents",
                _ => null
            },
            Breadcrumb = [("Home", "/"), (name, null)]
        };

        if (name == ContentSeeder.NotFoundPageName)
        {
            model.Breadcrumb = [];
            return View("~/Views/Pages/NotFound.cshtml", model);
        }

        return Page("ContentPage", model);
    }
}

public sealed class PaymentsPageController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public IActionResult Index(CancellationToken cancellationToken)
    {
        var page = CurrentPage!;
        return Page("Payments", new PaymentsLandingViewModel
        {
            Title = "Make a payment | Chelmsford College",
            MetaDescription = "Pay course deposits, materials fees and trips online. Parents and carers can pay on a student's behalf.",
            Section = "pay",
            Intro = page.Text(Props.Intro)
        });
    }
}

/// <summary>Folders have no page of their own.</summary>
public sealed class CampaignFolderController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public IActionResult Index(CancellationToken cancellationToken) => NotFoundPage();
}

/// <summary>
/// A campaign has no page of its own. Opening one (which is what Preview in the CMS does) shows the
/// homepage with that campaign in the hero, whether or not it is live now. Search engines are asked
/// not to index it, because it repeats the homepage.
/// </summary>
public sealed class CampaignController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor,
    HomePageBuilder builder)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var campaign = CurrentPage!;
        var home = campaign.Ancestors().FirstOrDefault(a => a.ContentType.Alias == Home);
        if (home is null)
        {
            return NotFoundPage();
        }

        var model = await builder.BuildAsync(home, SiteContent.ToCampaign(campaign), $"{Request.Scheme}://{Request.Host}", cancellationToken);
        model.NoIndex = true;
        return Page("Home", model);
    }
}

public sealed class PayableItemFolderController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public IActionResult Index(CancellationToken cancellationToken) => NotFoundPage();
}

public sealed class PayableItemController(
    ILogger<RenderController> logger,
    ICompositeViewEngine compositeViewEngine,
    IUmbracoContextAccessor umbracoContextAccessor)
    : SitePageController(logger, compositeViewEngine, umbracoContextAccessor)
{
    public IActionResult Index(CancellationToken cancellationToken) => NotFoundPage();
}
