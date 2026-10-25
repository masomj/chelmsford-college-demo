using System.Globalization;
using System.Text.Json;
using Chelmsford.Core.Events;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Umbraco;

/// <summary>
/// Sample content for the demo, created on first run only (when the CMS has no content at all).
/// Payable items are seeded separately, after the first sync has created the course pages they link to.
/// </summary>
public sealed class ContentSeeder(IContentService contentService, ILogger<ContentSeeder> logger)
{
    public const string NotFoundPageName = "Page not found";

    // Example addresses only: this is a demo, so no real college mailbox is named anywhere in the site.
    public const string ExampleCourseAdviceEmail = "courses@example.ac.uk";

    private const string AccessibilitySummary = "We want everyone to be able to use this website. This statement explains how accessible it is and how to tell us about problems.";

    private const string AccessibilityBody =
        "<h2>How accessible this website is</h2>" +
        "<p>This is a demonstration website prepared by Vanguard Digital Solutions for the Chelmsford College website tender. We aim to meet the Web Content Accessibility Guidelines (WCAG) 2.2 at level AA. This demo has not been independently audited yet.</p>" +
        "<p>We have built it so that you should be able to:</p><ul><li>use the whole site with a keyboard</li><li>zoom in up to 400% without text spilling off the screen</li><li>use the site with a screen reader such as NVDA, JAWS or VoiceOver</li><li>change colours, contrast and fonts in your browser</li></ul>" +
        "<h2>Known issues</h2>" +
        "<p>Some course descriptions come from the college's existing course data. We clean the formatting automatically, but a few descriptions may still have headings in an unusual order.</p>" +
        "<p>The homepage video is a placeholder until the college adds a captioned video.</p>" +
        "<h2>Tell us about a problem</h2>" +
        "<p>If you find a problem that is not listed here, email <a href=\"mailto:accessibility@example.ac.uk\">accessibility@example.ac.uk</a>. This is an example address for the demo. On the live site it would be the college's accessibility mailbox. We aim to reply within 5 working days.</p>" +
        "<h2>Enforcement procedure</h2>" +
        "<p>The Equality and Human Rights Commission (EHRC) is responsible for enforcing the accessibility regulations. If you are not happy with how we respond to your complaint, contact the Equality Advisory and Support Service (EASS).</p>" +
        "<h2>Preparation of this statement</h2>" +
        "<p>This statement was prepared on 25 October 2026. It is demo text and will be replaced by the college's own statement on the live site.</p>";

    private const string PrivacySummary = "What we collect when you use this website, why, and the choices you have about cookies.";

    private const string PrivacyBody =
        "<h2>This is a demonstration site</h2>" +
        "<p>This website was prepared for the Chelmsford College website tender. The students and applicants in it are made up. Please do not enter real personal details.</p>" +
        "<h2>Cookies we always set</h2>" +
        "<ul><li><strong>Cookie choice</strong>: remembers whether you accepted or rejected analytics cookies, for one year.</li><li><strong>Security</strong>: protects forms against misuse. Deleted when you close your browser.</li><li><strong>Payment session</strong>: remembers who you are paying for while you make a payment. Expires after 30 minutes.</li></ul>" +
        "<h2 id=\"cookie-settings\">Analytics cookies and cookie settings</h2>" +
        "<p>We only load analytics if you choose Accept in the cookie message. They help us understand which pages people use, so we can improve them. You can change your choice at any time with the Cookie settings link at the bottom of every page.</p>" +
        "<h2>Fonts</h2>" +
        "<p>The site's font is stored on our own server. Nothing is loaded from Google or any other font service.</p>" +
        "<h2>Payments</h2>" +
        "<p>Card payments are taken by our payment provider on their own secure page. Your card details never reach the college's systems. We keep a record of what was paid, who it was for and who paid it.</p>" +
        "<h2>Who looked at this demo</h2>" +
        "<p>To show the tender panel how the site is used, the demo keeps a short record of page visits and of sign-ins to the CMS: the time, the page address (without anything after a question mark), the sign-in name the panel was given, the first part of the visitor's IP address, and the browser name. Full IP addresses are not kept. Records are deleted after 90 days.</p>" +
        "<h2>Contact</h2>" +
        "<p>For questions about your data, email <a href=\"mailto:dataprotection@example.ac.uk\">dataprotection@example.ac.uk</a>. This is an example address for the demo.</p>";

    private const string ContactBody =
        "<p>Moulsham Street, Chelmsford, Essex CM2 0JQ</p><p>Email <a href=\"mailto:courses@example.ac.uk\">courses@example.ac.uk</a> for course advice. This is an example address for the demo.</p>";

    private static void SetStatement(IContent node, string summary, string body)
    {
        node.SetValue(Props.Summary, summary);
        node.SetValue(Props.Body, body);
    }

    /// <summary>
    /// Brings the accessibility statement, privacy page and contact page up to date on a site that was
    /// seeded with earlier text. A page is only rewritten while it still holds the old invented
    /// college email addresses, so anything an editor has changed since is left alone.
    /// </summary>
    public void RefreshDemoPages()
    {
        void Refresh(string name, string summary, string body)
        {
            foreach (var page in PagesNamed(name))
            {
                var current = page.GetValue<string>(Props.Body) ?? string.Empty;
                if (!current.Contains("@chelmsford.ac.uk", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SetStatement(page, summary, body);
                var result = contentService.SaveAndPublish(page, []);
                logger.LogInformation("Refreshed the demo text of {Name}: {Success}", name, result.Success);
            }
        }

        Refresh("Accessibility statement", AccessibilitySummary, AccessibilityBody);
        Refresh("Privacy and cookies", PrivacySummary, PrivacyBody);
        Refresh("Contact us", "Get in touch with the college.", ContactBody);
    }

    private List<IContent> PagesNamed(string name) =>
        contentService.GetRootContent()
            .Where(root => root.ContentType.Alias == Home)
            .SelectMany(home => contentService.AllChildren(home.Id))
            .Where(c => c.ContentType.Alias == ContentPage && c.Name == name)
            .ToList();

    public bool HasAnyContent() => contentService.GetRootContent().Any();

    public void SeedBaseContent()
    {
        var home = Create("Chelmsford College", Constants.System.Root, Home, n =>
        {
            n.SetValue(Props.MetaDescription, "Chelmsford College in Essex: courses for school leavers, adults and apprentices, open events, and online payments for deposits, materials and trips.");
            n.SetValue(Props.VideoHeading, "See what college life is like");
            n.SetValue(Props.VideoText, "Our students show you around the campus, the workshops and the people who'll support you.");
            n.SetValue(Props.VideoUrl, string.Empty);
        });

        var campaigns = Create("Campaigns", home.Id, CampaignFolder);
        Create("Open Evening campaign", campaigns.Id, Campaign, n =>
        {
            n.SetValue(Props.Label, "Open Evening");
            n.SetValue(Props.Heading, "Find your future at our Open Evening");
            n.SetValue(Props.Text, "Wednesday 18 November, 5pm to 8pm. Meet our tutors, tour the workshops and get help applying.");
            n.SetValue(Props.ButtonText, "Book your place");
            n.SetValue(Props.ButtonUrl, "/events/open-evening/");
            SetDate(n, Props.ShowUntil, new DateTime(2026, 11, 18, 20, 0, 0));
        });
        Create("Open Morning campaign", campaigns.Id, Campaign, n =>
        {
            n.SetValue(Props.Label, "Open Morning");
            n.SetValue(Props.Heading, "Come and see us at our Open Morning");
            n.SetValue(Props.Text, "Saturday 30 January, 10am to 1pm. Tour the campus and talk to our tutors.");
            n.SetValue(Props.ButtonText, "Book your place");
            n.SetValue(Props.ButtonUrl, "/events/open-morning/");
            SetDate(n, Props.ShowFrom, new DateTime(2026, 11, 18, 20, 0, 0));
            SetDate(n, Props.ShowUntil, new DateTime(2027, 1, 30, 13, 0, 0));
        });
        Create("Apply now campaign", campaigns.Id, Campaign, n =>
        {
            n.SetValue(Props.Label, "Apply now");
            n.SetValue(Props.Heading, "Applications for September 2027 are open");
            n.SetValue(Props.Text, "Find your course and apply in about 10 minutes.");
            n.SetValue(Props.ButtonText, "Find a course");
            n.SetValue(Props.ButtonUrl, "/courses/");
            SetDate(n, Props.ShowFrom, new DateTime(2027, 1, 30, 13, 0, 0));
        });

        Create("Courses", home.Id, CourseListing, n =>
            n.SetValue(Props.Intro, "Full-time courses for school leavers, and courses for adults who want to change career or get back into learning. Every course page comes straight from the college's course data."));

        var events = Create("Events", home.Id, EventListing, n =>
            n.SetValue(Props.Intro, "Open evenings, taster days and advice sessions. All events are free."));
        SeedEvents(events.Id);

        Create("Make a payment", home.Id, PaymentsPage, n =>
            n.SetValue(Props.Intro, "Pay course deposits, materials fees and trips online. Parents and carers can pay on a student's behalf."));

        Create("Accessibility statement", home.Id, ContentPage, n => SetStatement(n, AccessibilitySummary, AccessibilityBody));

        Create("Privacy and cookies", home.Id, ContentPage, n => SetStatement(n, PrivacySummary, PrivacyBody));
        SeedSectionPages(home.Id);

        Create(NotFoundPageName, home.Id, ContentPage, n =>
        {
            n.SetValue(Props.Summary, "If you typed the web address, check it is correct. If you pasted it, check you copied the whole address.");
            n.SetValue(Props.Body, "<p>You can <a href=\"/courses/\">search our courses</a>, see <a href=\"/events/\">upcoming events</a> or go to the <a href=\"/\">homepage</a>.</p>");
        });

        Create("Payable items", Constants.System.Root, PayableItemFolder);
        logger.LogInformation("Seeded the demo content");
    }

    /// <summary>Short landing pages so every navigation link goes somewhere real. Editors replace them on the live site.</summary>
    private void SeedSectionPages(int homeId)
    {
        const string demoNote = "<p class=\"demo-note\">This page is a short placeholder in the demo. On the live site, college staff write and update it in the CMS.</p>";

        void Page(string name, string summary, string body) =>
            Create(name, homeId, ContentPage, n =>
            {
                n.SetValue(Props.Summary, summary);
                n.SetValue(Props.Body, body + demoNote);
            });

        Page("Adult learners", "Courses for adults who want to change career, gain a qualification or get back into learning.",
            "<p>Many of our courses for adults are free or funded, depending on your age, your current qualifications and your income.</p>" +
            "<ul><li><a href=\"/courses/?funding=Funding%20for%20adults\">See courses with funding for adults</a></li><li><a href=\"/events/?type=Advice%20and%20guidance\">Come to an advice evening</a></li></ul>");
        Page("Apprenticeships", "Earn a wage while you train with an employer, and gain a nationally recognised qualification.",
            "<p>As an apprentice you spend most of your week at work and study with us for the rest. You are paid from day one.</p>" +
            "<ul><li><a href=\"/courses/\">Browse courses</a></li><li><a href=\"/events/\">Find an event</a></li></ul>");
        Page("Employers", "Recruit an apprentice, train your staff or work with us on a project.",
            "<p>Our employer team helps businesses of every size find and train the people they need.</p>" +
            "<ul><li><a href=\"/events/?type=Employers\">Join an employer event</a></li></ul>");
        Page("Parents and carers", "Information to help you support a young person choosing and starting college.",
            "<p>You can come to any of our open events with your young person, and pay for trips, materials and deposits on their behalf without an account.</p>" +
            "<ul><li><a href=\"/events/?type=Open%20events\">Open events</a></li><li><a href=\"/make-a-payment/\">Make a payment for a student</a></li></ul>");
        Page("Current students", "Links and support for students already studying with us.",
            "<ul><li><a href=\"/make-a-payment/\">Pay for materials and trips</a></li><li><a href=\"/events/\">Events</a></li></ul>");
        Page("Jobs", "Work at Chelmsford College.", "<p>Current vacancies are listed here on the live site.</p>");
        Page("Contact us", "Get in touch with the college.", ContactBody);
        Page("Apply", "Apply for a course at Chelmsford College.",
            "<p>On the live site, this button takes you to the college's online application form, with the course already filled in. It takes about 10 minutes.</p>" +
            "<p>For this demo, <a href=\"/courses/\">find a course</a> or <a href=\"/events/\">book an open event</a>.</p>");
    }

    private void SeedEvents(int parentId)
    {
        var moulsham = "Moulsham Street campus";
        var princes = "Princes Road campus";
        var moulshamAddress = "Chelmsford College\nMoulsham Street\nChelmsford\nEssex CM2 0JQ";
        var princesAddress = "Chelmsford College\nPrinces Road\nChelmsford\nEssex CM2 9DE";

        AddEvent(parentId, "Open Evening", EventTypes.OpenEvents, new DateTime(2026, 11, 18, 17, 0, 0), new DateTime(2026, 11, 18, 20, 0, 0), moulsham, moulshamAddress, 300,
            "Explore the campus, see the workshops and studios in action, and talk to the tutors who will teach you.",
            "<p>Explore our campus, see the workshops and studios in action and talk to the tutors who will teach you. Drop in at any time between 5pm and 8pm.</p>" +
            "<ul><li>Tours of our specialist workshops, salons and kitchens</li><li>Advice on courses, funding and support</li><li>Help with your application on the night</li></ul>" +
            "<p>Bring a parent, carer or friend. Our learning support team will be there if you want to talk about extra help with your studies.</p>");

        AddEvent(parentId, "Adult Learner Advice Evening", EventTypes.Advice, new DateTime(2026, 11, 24, 16, 0, 0), new DateTime(2026, 11, 24, 19, 0, 0), princes, princesAddress, 40,
            "Thinking about returning to learning? Get one-to-one advice on courses, funding and fitting study around work.",
            "<p>If you are thinking about returning to learning, come and talk to our advisers. We will help you find a course that fits around work and family.</p>" +
            "<ul><li>One-to-one advice on courses and qualifications</li><li>Help checking whether your course could be free or funded</li><li>Information about Access to Higher Education courses</li></ul>");

        AddEvent(parentId, "Apprenticeship Employer Breakfast", EventTypes.Employers, new DateTime(2026, 12, 3, 8, 0, 0), new DateTime(2026, 12, 3, 9, 30, 0), moulsham, moulshamAddress, 30,
            "Find out how apprenticeships can help your business grow, over breakfast with our employer team.",
            "<p>Join our employer team for breakfast and find out how taking on an apprentice could help your business.</p>" +
            "<ul><li>How apprenticeship funding works for employers of different sizes</li><li>What an apprentice's week looks like</li><li>How we help you recruit and support an apprentice</li></ul>");

        AddEvent(parentId, "Construction and Trades Taster Day", EventTypes.TasterDays, new DateTime(2027, 1, 16, 10, 0, 0), new DateTime(2027, 1, 16, 13, 0, 0), princes, princesAddress, 25,
            "Try carpentry, plumbing and painting and decorating in our workshops before you apply.",
            "<p>Spend a morning in our construction workshops and try a trade before you choose a course. You will work alongside our tutors and current students.</p>" +
            "<ul><li>Short practical sessions in carpentry, plumbing and painting and decorating</li><li>All tools and safety equipment provided</li><li>Wear clothes you don't mind getting dusty</li></ul>");

        AddEvent(parentId, "Open Morning", EventTypes.OpenEvents, new DateTime(2027, 1, 30, 10, 0, 0), new DateTime(2027, 1, 30, 13, 0, 0), moulsham, moulshamAddress, 300,
            "Tour the campus, meet our tutors and find out about courses starting in September 2027.",
            "<p>Our Open Morning is a chance to tour the campus, meet our tutors and find out about courses starting in September 2027.</p>" +
            "<ul><li>Campus tours throughout the morning</li><li>Subject areas open with tutors to talk to</li><li>Advice on applying, funding and support</li></ul>");

        AddEvent(parentId, "Fashion and Textiles Showcase", EventTypes.Showcases, new DateTime(2027, 6, 10, 18, 0, 0), new DateTime(2027, 6, 10, 20, 30, 0), moulsham, moulshamAddress, 150,
            "See garments, costumes and textiles designed and made by our fashion students this year.",
            "<p>Our fashion and textiles students present the garments, costumes and textile pieces they have designed and made this year.</p>" +
            "<p>Everyone is welcome, including families, friends and anyone thinking of studying fashion with us.</p>");
    }

    private void AddEvent(int parentId, string name, string type, DateTime starts, DateTime ends, string venue, string address, int capacity, string summary, string body) =>
        Create(name, parentId, Event, n =>
        {
            SetDropdown(n, Props.EventType, type);
            SetDate(n, Props.StartsAt, starts);
            SetDate(n, Props.EndsAt, ends);
            n.SetValue(Props.Venue, venue);
            n.SetValue(Props.Address, address);
            n.SetValue(Props.Summary, summary);
            n.SetValue(Props.Body, body);
            n.SetValue(Props.Capacity, capacity);
            n.SetValue(Props.BookingOpen, true);
        });

    /// <summary>Seeds payable items once the course pages exist. Only when the folder is empty.</summary>
    public void SeedPayableItems()
    {
        var folder = contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == PayableItemFolder);
        if (folder is null)
        {
            return;
        }

        if (contentService.AllChildren(folder.Id, 1).Count > 0)
        {
            return;
        }

        // Course pages are created from the college's data on the first sync, so which courses a demo
        // payment applies to is worked out from their names at run time, not from fixed course IDs.
        var courseKeys = CourseKeysByName();

        string Courses(params string[] words)
        {
            var pattern = new System.Text.RegularExpressions.Regex(@"\b(" + string.Join("|", words) + @")\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return string.Join(",", courseKeys.Where(c => pattern.IsMatch(c.Name)).Select(c => PublishedValues.Udi(c.Key)));
        }

        var creative = new[] { "fashion", "textiles?", "graphic", "media", "performing", "design", "creative", "art" };

        AddItem(folder.Id, "Creative industries course deposit", "Course deposit",
            "Secures your place on a creative industries course before you enrol. It comes off your first materials fee.",
            50.00m, Courses(creative), new DateTime(2027, 8, 1), null, "DEP-CRE", null);
        AddItem(folder.Id, "Fashion and Textiles materials", "Materials fee",
            "Fabrics, threads, pattern paper and a basic sewing kit for the year.",
            150.00m, Courses("fashion", "textiles?"), new DateTime(2026, 11, 30), null, "MAT-TEX", null);
        AddItem(folder.Id, "V&A and Fashion and Textile Museum visit", "Trip",
            "A day in London visiting the V&A's fashion galleries and the Fashion and Textile Museum. Coach travel and entry included.",
            35.00m, Courses("fashion", "textiles?"), new DateTime(2027, 1, 28), 12, "TRP-CRE-0204", new DateTime(2027, 2, 4, 9, 0, 0));
        AddItem(folder.Id, "Carpentry workshop safety kit", "Materials fee",
            "Safety boots, glasses, ear defenders and a workshop apron. Yours to keep.",
            25.00m, Courses("carpentry", "joinery"), new DateTime(2026, 11, 30), null, "MAT-CARP", null);

        logger.LogInformation("Seeded the demo payable items ({Courses} course pages available to link)", courseKeys.Count);
    }

    private void AddItem(int parentId, string name, string type, string description, decimal amount, string courses, DateTime due, int? places, string costCode, DateTime? takesPlaceOn) =>
        Create(name, parentId, PayableItem, n =>
        {
            SetDropdown(n, Props.ItemType, type);
            n.SetValue(Props.Description, description);
            n.SetValue(Props.Amount, amount);
            n.SetValue(Props.Courses, courses);
            SetDate(n, Props.DueDate, due);
            if (places is { } p)
            {
                n.SetValue(Props.Places, p);
            }

            n.SetValue(Props.CostCode, costCode);
            if (takesPlaceOn is { } t)
            {
                SetDate(n, Props.TakesPlaceOn, t);
            }
        });

    private List<(string Name, Guid Key)> CourseKeysByName()
    {
        var map = new List<(string Name, Guid Key)>();
        foreach (var root in contentService.GetRootContent())
        {
            foreach (var listing in contentService.AllChildren(root.Id).Where(c => c.ContentType.Alias == CourseListing))
            {
                foreach (var course in contentService.AllChildren(listing.Id))
                {
                    if (course.ContentType.Alias == Course && course.Name is { } courseName)
                    {
                        map.Add((courseName, course.Key));
                    }
                }
            }
        }

        return map;
    }

    private IContent Create(string name, int parentId, string alias, Action<IContent>? set = null)
    {
        var node = contentService.Create(name, parentId, alias);
        set?.Invoke(node);
        var result = contentService.SaveAndPublish(node, []);
        if (!result.Success)
        {
            logger.LogWarning("Seeding {Name} ({Alias}) did not publish: {Status}", name, alias, result.Result);
        }

        return node;
    }

    /// <summary>Dropdowns store a JSON array; a textstring fallback stores the plain value.</summary>
    private static void SetDropdown(IContent node, string alias, string value)
    {
        var editor = node.Properties[alias]?.PropertyType.PropertyEditorAlias;
        node.SetValue(alias, editor == Constants.PropertyEditors.Aliases.DropDownListFlexible ? JsonSerializer.Serialize(new[] { value }) : value);
    }

    /// <summary>Dates are UK wall-clock times. The newer date-time editors store JSON; the classic one a DateTime.</summary>
    private static void SetDate(IContent node, string alias, DateTime value)
    {
        var editor = node.Properties[alias]?.PropertyType.PropertyEditorAlias;
        if (editor == Constants.PropertyEditors.Aliases.DateTime)
        {
            node.SetValue(alias, value);
        }
        else if (editor == Constants.PropertyEditors.Aliases.DateTimeUnspecified)
        {
            node.SetValue(alias, JsonSerializer.Serialize(new { date = value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = (string?)null }));
        }
        else
        {
            node.SetValue(alias, value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
    }
}
