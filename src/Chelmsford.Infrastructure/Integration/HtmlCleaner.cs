using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Chelmsford.Core.Common;
using Ganss.Xss;

namespace Chelmsford.Infrastructure.Integration;

/// <summary>Turns the raw HTML stored in the college's tables into clean, accessible, safe markup.</summary>
public interface IHtmlCleaner
{
    /// <summary>Returns cleaned HTML, or null when the value is missing or has no content.</summary>
    string? Clean(string? rawHtml);
}

/// <summary>
/// Cleans course HTML from the source system. It removes Bootstrap 3 glyphicon icons, turns
/// "button" links into plain links, strips embeds, wrappers, inline styles and classes, promotes
/// bold-only paragraphs to headings, and sanitises to a short allowlist. The output depends only on
/// the input, so unchanged source data always produces identical HTML.
/// </summary>
public sealed class HtmlCleaner : IHtmlCleaner
{
    private const char NonBreakingSpace = ' ';

    private static readonly string[] AllowedTags = ["p", "ul", "ol", "li", "strong", "em", "br", "h3", "h4", "a"];
    private static readonly string[] AllowedSchemes = ["http", "https", "mailto"];

    /// <summary>Elements removed with everything inside them.</summary>
    private const string DroppedElements = "script, style, iframe, embed, object, noscript, template, svg, video, audio, img, form, input, button, select, textarea";

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase) { "P", "LI", "H3", "H4" };

    private readonly HtmlSanitizer _sanitizer;

    public HtmlCleaner()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(AllowedTags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedSchemes = new HashSet<string>(AllowedSchemes, StringComparer.OrdinalIgnoreCase),
            UriAttributes = new HashSet<string>(["href"], StringComparer.OrdinalIgnoreCase),
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedCssClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

        // Wrappers such as <div>, <span> and <font> are removed but their text is kept.
        _sanitizer = new HtmlSanitizer(options) { KeepChildNodes = true };
    }

    public string? Clean(string? rawHtml)
    {
        var value = SourceText.Clean(rawHtml);
        if (value is null)
        {
            return null;
        }

        var prepared = Prepare(value);
        var sanitised = _sanitizer.Sanitize(prepared);
        var tidied = Tidy(sanitised);
        return SourceText.PlainText(tidied).Length == 0 ? null : tidied;
    }

    /// <summary>Structural fixes that need the original classes and tags, before sanitising removes them.</summary>
    private static string Prepare(string html)
    {
        var body = ParseBody(html);

        foreach (var element in body.QuerySelectorAll(DroppedElements).ToList())
        {
            element.Remove();
        }

        foreach (var icon in body.QuerySelectorAll("span[class*='glyphicon'], i[class*='glyphicon'], span[class*='fa-'], i[class*='fa-']").ToList())
        {
            icon.Remove();
        }

        foreach (var link in body.QuerySelectorAll("a").ToList())
        {
            var href = link.GetAttribute("href")?.Trim();
            if (!IsAllowedHref(href))
            {
                // In-page anchors ("#courselist"), relative and javascript: links go nowhere on the new site.
                Unwrap(link);
                continue;
            }

            // Bootstrap "btn" links become ordinary links: only the destination survives.
            foreach (var attribute in link.Attributes.Select(a => a.Name).Where(n => n != "href").ToList())
            {
                link.RemoveAttribute(attribute);
            }

            link.SetAttribute("href", href!);
        }

        foreach (var heading in body.QuerySelectorAll("h1, h2, h5, h6").ToList())
        {
            // Course pages own the h1/h2 levels, so content headings start at h3.
            Rename(heading, heading.LocalName is "h1" or "h2" ? "h3" : "h4");
        }

        foreach (var paragraph in body.QuerySelectorAll("p").ToList())
        {
            if (SoleBoldChild(paragraph) is { } bold)
            {
                var heading = paragraph.Owner!.CreateElement("h3");
                heading.TextContent = Normalise(bold.TextContent).Trim();
                paragraph.Replace(heading);
            }
        }

        return body.InnerHtml;
    }

    /// <summary>Whitespace and empty-element tidying on the sanitised markup.</summary>
    private static string Tidy(string html)
    {
        var body = ParseBody(html);

        foreach (var text in Descendants(body).OfType<IText>().ToList())
        {
            text.Data = Normalise(text.Data);
        }

        // Empty inline formatting (e.g. <strong>&nbsp;</strong>) collapses to a single space.
        foreach (var inline in body.QuerySelectorAll("strong, em").Where(e => IsBlank(e)).ToList())
        {
            inline.Replace(inline.Owner!.CreateTextNode(" "));
        }

        foreach (var block in body.QuerySelectorAll("p, li, h3, h4").ToList())
        {
            TrimTrailing(block);
            TrimLeading(block);
        }

        foreach (var block in body.QuerySelectorAll("p, h3, h4, li").Where(e => IsBlank(e)).ToList())
        {
            block.Remove();
        }

        foreach (var list in body.QuerySelectorAll("ul, ol").Where(l => l.Children.Length == 0).ToList())
        {
            list.Remove();
        }

        // Top-level whitespace between blocks (left over from _x000D_ line breaks) is dropped.
        foreach (var node in body.ChildNodes.OfType<IText>().Where(t => string.IsNullOrWhiteSpace(t.Data)).ToList())
        {
            node.Remove();
        }

        WrapLooseInlineContent(body);
        return body.InnerHtml.Trim();
    }

    /// <summary>
    /// Text left at the top level once wrappers such as &lt;div&gt; are removed is put into paragraphs,
    /// so every piece of content sits in a block element.
    /// </summary>
    private static void WrapLooseInlineContent(IElement body)
    {
        IElement? paragraph = null;
        foreach (var node in body.ChildNodes.ToList())
        {
            var isBlock = node is IElement element && element.LocalName is "p" or "ul" or "ol" or "h3" or "h4";
            if (isBlock)
            {
                if (paragraph is not null)
                {
                    TrimTrailing(paragraph);
                    TrimLeading(paragraph);
                    if (IsBlank(paragraph))
                    {
                        paragraph.Remove();
                    }
                }

                paragraph = null;
                continue;
            }

            if (paragraph is null)
            {
                paragraph = body.Owner!.CreateElement("p");
                body.InsertBefore(paragraph, node);
            }

            paragraph.AppendChild(node);
        }

        if (paragraph is not null)
        {
            TrimTrailing(paragraph);
            TrimLeading(paragraph);
            if (IsBlank(paragraph))
            {
                paragraph.Remove();
            }
        }
    }

    private static IElement ParseBody(string html)
    {
        var parser = new HtmlParser();
        var document = parser.ParseDocument(string.Empty);
        var body = document.Body!;
        body.InnerHtml = html;
        return body;
    }

    private static bool IsAllowedHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The bold element when a paragraph contains nothing but one &lt;strong&gt; or &lt;b&gt; (plus whitespace).</summary>
    private static IElement? SoleBoldChild(IElement paragraph)
    {
        IElement? bold = null;
        foreach (var node in paragraph.ChildNodes)
        {
            switch (node)
            {
                case IText text when string.IsNullOrWhiteSpace(Normalise(text.Data)):
                    continue;
                case IElement { LocalName: "br" }:
                    continue;
                case IElement { LocalName: "strong" or "b" } element when bold is null:
                    bold = element;
                    continue;
                default:
                    return null;
            }
        }

        return bold is not null && Normalise(bold.TextContent).Trim().Length > 0 ? bold : null;
    }

    private static void TrimTrailing(IElement block)
    {
        while (block.LastChild is { } last)
        {
            if (last is IText text)
            {
                text.Data = text.Data.TrimEnd();
                if (text.Data.Length > 0)
                {
                    return;
                }

                text.Remove();
            }
            else if (last is IElement { LocalName: "br" })
            {
                block.RemoveChild(last);
            }
            else if (last is IElement element && !BlockElements.Contains(element.TagName) && element.LocalName is not ("ul" or "ol"))
            {
                TrimTrailing(element);
                return;
            }
            else
            {
                return;
            }
        }
    }

    private static void TrimLeading(IElement block)
    {
        while (block.FirstChild is { } first)
        {
            if (first is IText text)
            {
                text.Data = text.Data.TrimStart();
                if (text.Data.Length > 0)
                {
                    return;
                }

                text.Remove();
            }
            else if (first is IElement { LocalName: "br" })
            {
                block.RemoveChild(first);
            }
            else if (first is IElement element && !BlockElements.Contains(element.TagName) && element.LocalName is not ("ul" or "ol"))
            {
                TrimLeading(element);
                return;
            }
            else
            {
                return;
            }
        }
    }

    private static bool IsBlank(IElement element) =>
        Normalise(element.TextContent).Trim().Length == 0;

    private static string Normalise(string text) => text.Replace(NonBreakingSpace, ' ');

    private static IEnumerable<INode> Descendants(INode node)
    {
        foreach (var child in node.ChildNodes)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Unwrap(IElement element)
    {
        var parent = element.Parent;
        if (parent is null)
        {
            return;
        }

        while (element.FirstChild is { } child)
        {
            parent.InsertBefore(child, element);
        }

        element.Remove();
    }

    private static void Rename(IElement element, string tagName)
    {
        var replacement = element.Owner!.CreateElement(tagName);
        while (element.FirstChild is { } child)
        {
            replacement.AppendChild(child);
        }

        element.Replace(replacement);
    }
}
