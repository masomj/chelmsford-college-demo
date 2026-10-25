using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Chelmsford.Web.Services;

public static class HtmlExtensions
{
    /// <summary>
    /// Renders a responsive picture (WebP and JPEG at 480/960/1600 widths) or, if the image isn't
    /// available, a tinted block so the layout holds and nothing shows as broken.
    /// </summary>
    public static IHtmlContent Picture(this IHtmlHelper html, Picture? picture, string sizes, string cssClass, bool eager = false, string? alt = null)
    {
        var assets = html.ViewContext.HttpContext.RequestServices.GetRequiredService<SiteAssets>();
        var enc = HtmlEncoder.Default;
        var altText = enc.Encode(alt ?? picture?.Alt ?? string.Empty);
        var loading = eager ? "eager\" fetchpriority=\"high" : "lazy";

        if (picture?.Url is { } url)
        {
            return new HtmlString($"<img class=\"{enc.Encode(cssClass)}\" src=\"{enc.Encode(url)}\" alt=\"{altText}\" loading=\"{loading}\" decoding=\"async\">");
        }

        if (picture?.Slug is { } slug && assets.HasPhoto(slug))
        {
            var s = enc.Encode(slug);
            return new HtmlString(
                $"<picture><source type=\"image/webp\" srcset=\"{SiteAssets.SrcSet(s, "webp")}\" sizes=\"{enc.Encode(sizes)}\">" +
                $"<img class=\"{enc.Encode(cssClass)}\" src=\"{SiteAssets.Src(s, 960)}\" srcset=\"{SiteAssets.SrcSet(s, "jpg")}\" sizes=\"{enc.Encode(sizes)}\" " +
                $"alt=\"{altText}\" width=\"960\" height=\"640\" loading=\"{loading}\" decoding=\"async\"></picture>");
        }

        return new HtmlString($"<span class=\"{enc.Encode(cssClass)} img-fallback\" aria-hidden=\"true\"></span>");
    }

    public static bool IsCurrent(this IHtmlHelper html, string section) =>
        html.ViewData.Model is Models.PageViewModel page && string.Equals(page.Section, section, StringComparison.Ordinal);

    /// <summary>
    /// aria-current for a navigation item. "page" only when the visitor is on exactly that page;
    /// "true" when they are somewhere inside the same section (for example a single course under Courses).
    /// </summary>
    public static IHtmlContent AriaCurrent(this IHtmlHelper html, string section, string? url = null)
    {
        if (!html.IsCurrent(section))
        {
            return HtmlString.Empty;
        }

        var path = html.ViewContext.HttpContext.Request.Path.Value ?? "/";
        var exact = url is not null && string.Equals(path.TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        return new HtmlString(exact ? " aria-current=\"page\"" : " aria-current=\"true\"");
    }

    /// <summary>Rich text from the CMS or the college's data, sanitised before it is written to the page.</summary>
    public static IHtmlContent RichText(this IHtmlHelper html, string? value) => Services.RichText.Render(value);
}
