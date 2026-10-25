using Chelmsford.Web.Umbraco;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using static Chelmsford.Web.Umbraco.DocTypes;

namespace Chelmsford.Web.Services;

public sealed record CampaignInfo(string Label, string Heading, string? Text, string? ButtonText, string? ButtonUrl, Picture? Image);

public sealed record EventInfo(
    Guid Key,
    string Name,
    string Url,
    string Type,
    DateTime StartsAt,
    DateTime? EndsAt,
    string? Venue,
    string? Address,
    string? Summary,
    string? BodyHtml,
    Picture? Image,
    int? Capacity,
    bool BookingOpen)
{
    public string When => $"{UkTime.DayAndDate(StartsAt)}, {UkTime.TimeRange(StartsAt, EndsAt)}";
}

/// <summary>Reads the published CMS content the pages need, by document type rather than by fixed IDs.</summary>
public sealed class SiteContent(
    IUmbracoContextFactory contextFactory,
    IDocumentNavigationQueryService navigation)
{
    /// <summary>Runs <paramref name="read"/> with the published content cache, inside or outside a request.</summary>
    public T Read<T>(Func<IPublishedContentCache, T> read)
    {
        using var reference = contextFactory.EnsureUmbracoContext();
        return read(reference.UmbracoContext.Content!);
    }

    public IPublishedContent? Root(IPublishedContentCache cache, string alias)
    {
        if (!navigation.TryGetRootKeysOfType(alias, out var keys))
        {
            return null;
        }

        return keys.Select(k => cache.GetById(k)).FirstOrDefault(c => c is not null);
    }

    public IPublishedContent? Home(IPublishedContentCache cache) => Root(cache, DocTypes.Home);

    public IPublishedContent? HomeChild(IPublishedContentCache cache, string alias) =>
        Home(cache)?.Children()?.FirstOrDefault(c => c.ContentType.Alias == alias);

    public string? UrlOf(IPublishedContentCache cache, string alias) => HomeChild(cache, alias)?.Url();

    /// <summary>The first campaign that is live now (UK time), in the order editors sorted them.</summary>
    public CampaignInfo? LiveCampaign()
    {
        var now = UkTime.NowLocal;
        return Read(cache =>
        {
            var folder = HomeChild(cache, CampaignFolder);
            var live = folder?.Children()?
                .Where(c => c.ContentType.Alias == Campaign)
                .FirstOrDefault(c => (c.DateTime(Props.ShowFrom) is not { } from || from <= now)
                                     && (c.DateTime(Props.ShowUntil) is not { } until || until > now));
            return live is null ? null : ToCampaign(live);
        });
    }

    /// <summary>A campaign page as the homepage hero shows it, whether or not it is live now.</summary>
    public static CampaignInfo ToCampaign(IPublishedContent campaign)
    {
        var image = campaign.Media(Props.Image) is { } m ? Picture.FromUrl(m.Url, m.Alt) : null;
        return new CampaignInfo(
            campaign.Text(Props.Label) ?? string.Empty,
            campaign.Text(Props.Heading) ?? campaign.Name ?? string.Empty,
            campaign.Text(Props.Text),
            campaign.Text(Props.ButtonText),
            campaign.Text(Props.ButtonUrl),
            image);
    }

    public IReadOnlyList<EventInfo> Events() => Read(cache =>
        (IReadOnlyList<EventInfo>)(HomeChild(cache, EventListing)?.Children()?
            .Where(c => c.ContentType.Alias == Event)
            .Select(ToEvent)
            .OfType<EventInfo>()
            .OrderBy(e => e.StartsAt)
            .ToList() ?? []));

    public IReadOnlyList<EventInfo> UpcomingEvents()
    {
        var now = UkTime.NowLocal;
        return Events().Where(e => (e.EndsAt ?? e.StartsAt) >= now).ToList();
    }

    public EventInfo? EventByKey(Guid key) => Read(cache => cache.GetById(key) is { } c && c.ContentType.Alias == Event ? ToEvent(c) : null);

    public static EventInfo? ToEvent(IPublishedContent c)
    {
        if (c.DateTime(Props.StartsAt) is not { } starts)
        {
            return null;
        }

        var image = c.Media(Props.Image) is { } m ? Picture.FromUrl(m.Url, m.Alt) : null;
        return new EventInfo(
            c.Key,
            c.Name ?? string.Empty,
            c.Url(),
            c.Text(Props.EventType) ?? string.Empty,
            starts,
            c.DateTime(Props.EndsAt),
            c.Text(Props.Venue),
            c.Text(Props.Address),
            c.Text(Props.Summary),
            c.Html(Props.Body),
            image,
            c.Int(Props.Capacity),
            c.Bool(Props.BookingOpen));
    }

    /// <summary>Course ID → URL of its CMS page.</summary>
    public IReadOnlyDictionary<int, string> CourseUrls() => Read(cache =>
    {
        var map = new Dictionary<int, string>();
        foreach (var node in HomeChild(cache, CourseListing)?.Children() ?? [])
        {
            if (node.ContentType.Alias == Course && node.Int(Props.CourseId) is { } id)
            {
                map.TryAdd(id, node.Url());
            }
        }

        return (IReadOnlyDictionary<int, string>)map;
    });
}
