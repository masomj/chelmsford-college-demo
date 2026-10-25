using Chelmsford.Core.Events;
using Chelmsford.Web.Configuration;

namespace Chelmsford.Web.Services;

/// <summary>A photo to show: either a CMS media item or one of the college's prepared images.</summary>
public sealed record Picture(string? Slug, string? Url, string Alt)
{
    public static Picture FromSlug(string slug, string alt = "") => new(slug, null, alt);

    public static Picture FromUrl(string url, string alt = "") => new(null, url, alt);
}

/// <summary>
/// The college's logos and photos come from the tender pack and are mounted at run time. If they are
/// missing the site falls back to a text wordmark and tinted blocks, never a broken image.
/// </summary>
public sealed class SiteAssets(CollegePaths paths)
{
    public const string BasePath = "/college-assets/";
    public static readonly int[] Widths = [480, 960, 1600];

    private readonly Lazy<HashSet<string>> _files = new(() =>
        Directory.Exists(paths.ImagesPath)
            ? new HashSet<string>(Directory.EnumerateFiles(paths.ImagesPath).Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool HasColourLogo => _files.Value.Contains("logo-colour.png");

    public bool HasWhiteLogo => _files.Value.Contains("logo-white.png");

    public bool HasPhoto(string? slug) => slug is not null && _files.Value.Contains($"{slug}-960.jpg");

    /// <summary>True when the picture can be shown (a CMS image, or a prepared photo that exists).</summary>
    public bool CanShow(Picture? picture) => picture is not null && (picture.Url is not null || HasPhoto(picture.Slug));

    public static string Src(string slug, int width, string ext = "jpg") => $"{BasePath}{slug}-{width}.{ext}";

    public static string SrcSet(string slug, string ext) =>
        string.Join(", ", Widths.Select(w => $"{Src(slug, w, ext)} {w}w"));

    /// <summary>Default photo for a subject, so every course page has a relevant image.</summary>
    public static string SlugForSubject(string? subject)
    {
        var s = (subject ?? string.Empty).ToLowerInvariant();
        if (s.Contains("creative") || s.Contains("performing") || s.Contains("media") || s.Contains("art"))
        {
            return "fashion-textiles";
        }

        if (s.Contains("construction") || s.Contains("electrical") || s.Contains("engineering") || s.Contains("green"))
        {
            return "carpentry";
        }

        if (s.Contains("hair") || s.Contains("beauty"))
        {
            return "hairdressing";
        }

        if (s.Contains("catering") || s.Contains("hospitality"))
        {
            return "catering";
        }

        return s.Length % 2 == 0 ? "students-learning" : "group-of-learners";
    }

    public static string SlugForEventType(string? eventType, string? title = null)
    {
        var t = (title ?? string.Empty).ToLowerInvariant();
        if (t.Contains("fashion") || t.Contains("textile"))
        {
            return "fashion-textiles";
        }

        if (t.Contains("construction") || t.Contains("trades"))
        {
            return "carpentry";
        }

        return eventType switch
        {
            EventTypes.OpenEvents => "students-learning",
            EventTypes.Showcases => "fashion-textiles",
            EventTypes.TasterDays => "carpentry",
            EventTypes.Advice => "group-of-learners",
            EventTypes.Employers => "news-story",
            _ => "group-of-learners"
        };
    }

    /// <summary>Plain-English alt text for the prepared photos, used where the image carries meaning.</summary>
    public static string AltFor(string slug) => slug switch
    {
        "fashion-textiles" => "A fashion and textiles student at work on a garment",
        "carpentry" => "A student working on a joinery project in the carpentry workshop",
        "hairdressing" => "A hairdressing student styling a client's hair in the college salon",
        "catering" => "Catering students preparing food in a professional kitchen",
        "group-of-learners" => "A group of learners working together in a classroom",
        "news-story" => "A student holding an award, standing with members of staff",
        _ => "Students learning together at Chelmsford College"
    };
}
