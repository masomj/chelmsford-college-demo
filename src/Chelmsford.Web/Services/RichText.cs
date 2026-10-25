using System.Text.RegularExpressions;
using Ganss.Xss;
using Microsoft.AspNetCore.Html;

namespace Chelmsford.Web.Services;

/// <summary>
/// Cleans rich text that editors write in the CMS (or that arrives from the college's data) before it
/// is written into a page. Normal formatting is kept: headings, lists, links, images with alt text and
/// tables. Scripts, event handlers, inline styles and javascript: links are removed.
/// </summary>
public static partial class RichText
{
    private static readonly HtmlSanitizer Sanitizer = Build();

    /// <summary>Sanitised HTML as a string. Safe to pass to Html.Raw.</summary>
    public static string Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var clean = Sanitizer.Sanitize(html);
        return WrapTables(clean);
    }

    public static IHtmlContent Render(string? html) => new HtmlString(Clean(html));

    private static HtmlSanitizer Build()
    {
        var sanitizer = new HtmlSanitizer();

        // Links and images may be site-relative, http, https, mailto or tel. Everything else
        // (javascript:, data:, vbscript:) is dropped.
        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto", "tel" })
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        // Editors use classes for the few styled blocks the site defines (for example demo-note).
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        sanitizer.AllowedAttributes.Add("target");
        sanitizer.AllowedAttributes.Add("rel");
        sanitizer.AllowedAttributes.Add("loading");

        // Open-in-new-tab links must not be able to reach back into this page.
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement { Target: { Length: > 0 } } anchor)
            {
                anchor.SetAttribute("rel", "noopener noreferrer");
            }
        };

        return sanitizer;
    }

    /// <summary>
    /// A wide table scrolls sideways on a small screen, so the scrolling box has to be reachable by
    /// keyboard and have a name.
    /// </summary>
    private static string WrapTables(string html)
    {
        if (!html.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        html = TableStart().Replace(html, "<div class=\"table-scroll\" tabindex=\"0\" role=\"region\" aria-label=\"Table. Scroll sideways to see all of it.\"><table");
        return html.Replace("</table>", "</table></div>", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("<table", RegexOptions.IgnoreCase)]
    private static partial Regex TableStart();
}
