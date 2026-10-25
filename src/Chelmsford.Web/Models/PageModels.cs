using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Core.Payments;
using Chelmsford.Web.Services;

namespace Chelmsford.Web.Models;

/// <summary>What every page gives the layout: title, description, which nav item is current, structured data.</summary>
public abstract class PageViewModel
{
    public string Title { get; set; } = "Chelmsford College";
    public string? MetaDescription { get; set; }

    /// <summary>Main navigation section to mark with aria-current: courses, adults, apprenticeships, employers, events, parents, pay.</summary>
    public string? Section { get; set; }

    /// <summary>JSON-LD, already serialised.</summary>
    public string? JsonLd { get; set; }

    /// <summary>Adds bottom padding for the mobile sticky action bar.</summary>
    public bool HasStickyBar { get; set; }

    /// <summary>Hide the site navigation (the payment journey keeps people focused).</summary>
    public bool Focused { get; set; }

    /// <summary>Asks search engines not to index the page (for example a campaign preview that repeats the homepage).</summary>
    public bool NoIndex { get; set; }
}

public sealed class SimplePageViewModel : PageViewModel
{
    public string Heading { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? BodyHtml { get; set; }
    public IReadOnlyList<(string Text, string? Url)> Breadcrumb { get; set; } = [];
}

public sealed record SubjectCard(string Title, string Description, string Url, Picture Picture);

public sealed record RouteCard(string Title, string Description, string Url, bool Green);

public sealed class HomeViewModel : PageViewModel
{
    public CampaignInfo? Campaign { get; set; }
    public Picture HeroPicture { get; set; } = Picture.FromSlug("students-learning");
    public int CourseCount { get; set; }
    public IReadOnlyList<SubjectSummary> Subjects { get; set; } = [];
    public IReadOnlyList<RouteCard> Routes { get; set; } = [];
    public IReadOnlyList<SubjectCard> SubjectCards { get; set; } = [];
    public string VideoHeading { get; set; } = string.Empty;
    public string? VideoText { get; set; }
    public string? VideoEmbedUrl { get; set; }
    public Picture VideoPoster { get; set; } = Picture.FromSlug("group-of-learners");
    public IReadOnlyList<EventInfo> Events { get; set; } = [];
    public string EventsUrl { get; set; } = "/events/";
    public string CoursesUrl { get; set; } = "/courses/";
}

public sealed class CourseCard
{
    public required SyncedCourse Course { get; init; }
    public string Url { get; init; } = "#";
}

public sealed class CourseListingViewModel : PageViewModel
{
    public string? Intro { get; set; }
    public CourseSearchQuery Query { get; set; } = new();
    public CourseSearchResult Result { get; set; } = new([], [], [], [], [], [], 0);
    public IReadOnlyList<CourseCard> Cards { get; set; } = [];
    public string ListingUrl { get; set; } = "/courses/";
}

public sealed record CourseSection(string Id, string Title, string ShortTitle, string Html);

public sealed class CourseViewModel : PageViewModel
{
    public required SyncedCourse Course { get; init; }
    public string Intro { get; set; } = string.Empty;
    public Picture Picture { get; set; } = Picture.FromSlug("students-learning");
    public string EntrySummary { get; set; } = string.Empty;
    public string FeeLabel { get; set; } = "Contact us";

    /// <summary>Funding groups joined for the "At a glance" panel, or a prompt to ask.</summary>
    public string FundingLabel { get; set; } = "Contact us about funding";

    /// <summary>
    /// Short plain-text lines for the "At a glance" panel: a summary of the college's additional fees
    /// text, then the payable items (trips, materials, deposits) that apply to this course.
    /// </summary>
    public IReadOnlyList<string> AdditionalCosts { get; set; } = [];

    public IReadOnlyList<CourseSection> Sections { get; set; } = [];
    public string? StudentQuote { get; set; }
    public string? StudentName { get; set; }
    public string? VideoEmbedUrl { get; set; }
    public string? CampaignMessage { get; set; }
    public EventInfo? NextEvent { get; set; }
    public string CoursesUrl { get; set; } = "/courses/";
    public string ApplyUrl { get; set; } = "#apply";
    public string EnquireUrl { get; set; } = "#enquire";
}

public sealed class EventListingViewModel : PageViewModel
{
    public string? Intro { get; set; }
    public string? Type { get; set; }
    public string? Month { get; set; }
    public IReadOnlyList<string> Types { get; set; } = [];
    public IReadOnlyList<(string Key, string Label)> Months { get; set; } = [];
    public IReadOnlyList<EventInfo> Events { get; set; } = [];
    public string ListingUrl { get; set; } = "/events/";
}

public sealed class BookingForm
{
    public Guid EventKey { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? AttendeeType { get; set; }
    public int? People { get; set; } = 1;
}

public sealed class EventViewModel : PageViewModel
{
    public required EventInfo Event { get; init; }
    public Picture Picture { get; set; } = Picture.FromSlug("students-learning");
    public int? PlacesLeft { get; set; }
    public bool Booked { get; set; }
    public string? BookingProblem { get; set; }
    public BookingForm Form { get; set; } = new();
    public Dictionary<string, string> Errors { get; set; } = new(StringComparer.Ordinal);
    public string EventsUrl { get; set; } = "/events/";
    public bool IsPast { get; set; }

    public static readonly IReadOnlyList<string> AttendeeTypes = ["A school leaver", "A parent or carer", "An adult learner", "An employer"];
}

public sealed class PaymentsLandingViewModel : PageViewModel
{
    public string? Intro { get; set; }
}

/// <summary>Shared by every step of the payment journey.</summary>
public abstract class PayStepViewModel : PageViewModel
{
    protected PayStepViewModel()
    {
        Section = "pay";
        Focused = true;
    }

    public int Step { get; set; }
    public string? BackUrl { get; set; }
    public Dictionary<string, string> Errors { get; set; } = new(StringComparer.Ordinal);
    public string? Message { get; set; }
}

public sealed class PayMessageViewModel : PayStepViewModel
{
    public string Heading { get; set; } = string.Empty;
    public string? Body { get; set; }
    public string? LinkText { get; set; }
    public string? LinkUrl { get; set; }
}

public sealed record DemoStudentChoice(string StudentId, string Name, string Course);

public sealed class SignInViewModel : PayStepViewModel
{
    public IReadOnlyList<DemoStudentChoice> Students { get; set; } = [];
}

public sealed class LinkStartViewModel : PayStepViewModel
{
}

public sealed class LinkViewModel : PayStepViewModel
{
    public string Token { get; set; } = string.Empty;
    public required ResolvedPaymentLink Link { get; init; }
    public bool SomeoneElse { get; set; }
    public string? PayerName { get; set; }
    public string? PayerEmail { get; set; }
    public string? Relationship { get; set; }
}

public sealed class FindStudentForm
{
    public string? StudentId { get; set; }
    public string? Day { get; set; }
    public string? Month { get; set; }
    public string? Year { get; set; }
    public string? PayerName { get; set; }
    public string? PayerEmail { get; set; }
    public string? Relationship { get; set; }
}

public sealed class FindStudentViewModel : PayStepViewModel
{
    public FindStudentForm Form { get; set; } = new();

    public static readonly IReadOnlyList<string> Relationships = ["Parent", "Carer", "Guardian", "Other"];
}

public sealed class ItemsViewModel : PayStepViewModel
{
    public required PaymentSubject Subject { get; init; }
    public IReadOnlyList<DueItem> Items { get; set; } = [];
    public Guid? Selected { get; set; }
}

public sealed class ReviewViewModel : PayStepViewModel
{
    public required PaymentOrder Order { get; init; }
}

public sealed class TestCheckoutViewModel : PayStepViewModel
{
    public required PaymentOrder Order { get; init; }
}

public sealed class CompleteViewModel : PayStepViewModel
{
    public required PaymentOrder Order { get; init; }
    public string? ReceiptTo { get; set; }

    /// <summary>The item had already been paid when this payment arrived, so this one will be refunded.</summary>
    public bool Duplicate { get; set; }

    public bool Refunded { get; set; }
}

public sealed class AccessReportViewModel : PageViewModel
{
    public int Days { get; set; }
    public IReadOnlyList<Chelmsford.Core.Access.AccessDaySummary> Summary { get; set; } = [];
    public IReadOnlyList<Chelmsford.Core.Access.AccessLogEntry> CmsEvents { get; set; } = [];
    public IReadOnlyList<Chelmsford.Core.Access.AccessLogEntry> PageViews { get; set; } = [];
}

public sealed record RawField(string Column, string? Raw, string Label, string? Cleaned, bool CleanedIsHtml);

public sealed class DataFlowViewModel : PageViewModel
{
    public SyncRun? LastRun { get; set; }
    public bool JustSynced { get; set; }
    public int SelectedCourseId { get; set; }
    public IReadOnlyList<(int Id, string Title)> CourseOptions { get; set; } = [];
    public IReadOnlyList<RawField> Fields { get; set; } = [];
    public SyncedCourse? Cleaned { get; set; }
    public string? SourceError { get; set; }
    public string? CourseUrl { get; set; }
}
