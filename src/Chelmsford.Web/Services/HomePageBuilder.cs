using Chelmsford.Core.Courses;
using Chelmsford.Web.Models;
using Chelmsford.Web.Umbraco;
using Umbraco.Cms.Core.Models.PublishedContent;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Services;

/// <summary>
/// Builds the homepage view model. The homepage uses it for the campaign that is live now; a campaign
/// page uses it to show that one campaign in the hero, which is what editors see in the CMS preview.
/// </summary>
public sealed class HomePageBuilder(ICourseSearchService courses, SiteContent content)
{
    public async Task<HomeViewModel> BuildAsync(IPublishedContent home, CampaignInfo? campaign, string origin, CancellationToken cancellationToken)
    {
        var subjects = await courses.SubjectsAsync(cancellationToken);

        string SubjectUrl(string subject) => "/courses/?subject=" + Uri.EscapeDataString(subject);
        string? Match(params string[] words) =>
            subjects.FirstOrDefault(s => words.Any(w => s.Subject.Contains(w, StringComparison.OrdinalIgnoreCase)))?.Subject;

        var cards = new List<SubjectCard>();
        if (Match("Creative") is { } creative)
        {
            cards.Add(new SubjectCard("Creative industries", "Fashion, media, graphic design and performing arts", SubjectUrl(creative), Picture.FromSlug("fashion-textiles")));
        }

        if (Match("Construction") is { } construction)
        {
            cards.Add(new SubjectCard("Construction and trades", "Carpentry, plumbing, painting and electrical", SubjectUrl(construction), Picture.FromSlug("carpentry")));
        }

        if (Match("Hair") is { } hair)
        {
            cards.Add(new SubjectCard("Hair and beauty", "Salon-standard training from Level 1 to 3", SubjectUrl(hair), Picture.FromSlug("hairdressing")));
        }

        if (Match("Catering", "Hospitality") is { } catering)
        {
            cards.Add(new SubjectCard("Catering and hospitality", "Train in our professional kitchens", SubjectUrl(catering), Picture.FromSlug("catering")));
        }

        var poster = home.Media(Props.VideoPoster) is { } p ? Picture.FromUrl(p.Url, p.Alt) : Picture.FromSlug("group-of-learners");
        var model = new HomeViewModel
        {
            Title = "Chelmsford College: courses, events and payments",
            MetaDescription = home.Text(Props.MetaDescription),
            Campaign = campaign,
            HeroPicture = campaign?.Image ?? Picture.FromSlug("students-learning", "Students talking and working together at Chelmsford College"),
            CourseCount = subjects.Sum(s => s.CourseCount),
            Subjects = subjects,
            Routes =
            [
                new RouteCard("Leaving school", "Courses for 16 to 18 year olds, from Entry level to T Levels", "/courses/?funding=" + Uri.EscapeDataString(CourseVocabulary.Funding16To18), false),
                new RouteCard("Adult learners", "Funded courses that help you change career or get back into learning", "/adult-learners/", true),
                new RouteCard("Apprenticeships", "Earn a wage while you train with an employer", "/apprenticeships/", true),
                new RouteCard("Employers", "Recruit apprentices and train your staff", "/employers/", false)
            ],
            SubjectCards = cards.Take(3).ToList(),
            VideoHeading = home.Text(Props.VideoHeading) ?? "See what college life is like",
            VideoText = home.Text(Props.VideoText),
            VideoEmbedUrl = TextHelpers.VideoEmbedUrl(home.Text(Props.VideoUrl)),
            VideoPoster = poster,
            Events = content.UpcomingEvents().Take(3).ToList()
        };

        model.JsonLd = TextHelpers.JsonLd(new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "CollegeOrUniversity",
            ["name"] = "Chelmsford College",
            ["url"] = origin + "/",
            ["logo"] = origin + SiteAssets.BasePath + "logo-colour.png",
            ["address"] = new Dictionary<string, object?>
            {
                ["@type"] = "PostalAddress",
                ["streetAddress"] = "Moulsham Street",
                ["addressLocality"] = "Chelmsford",
                ["addressRegion"] = "Essex",
                ["postalCode"] = "CM2 0JQ",
                ["addressCountry"] = "GB"
            }
        });

        return model;
    }
}
