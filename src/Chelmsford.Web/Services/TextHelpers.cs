using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chelmsford.Core.Common;

namespace Chelmsford.Web.Services;

public static partial class TextHelpers
{
    private static readonly JsonSerializerOptions JsonLdOptions = new()
    {
        // Keep "<" escaped so JSON-LD can never close its script element early.
        Encoder = JavaScriptEncoder.Default,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static string JsonLd(object value) => JsonSerializer.Serialize(value, JsonLdOptions);

    /// <summary>First sentence of some HTML, as plain text.</summary>
    public static string FirstSentence(string? html, int max = 220)
    {
        var text = SourceText.PlainText(html);
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var match = SentenceEnd().Match(text);
        var sentence = match.Success && match.Index + 1 <= max ? text[..(match.Index + 1)] : text;
        return Truncate(sentence, max);
    }

    /// <summary>Plain text cut at a word boundary, with an ellipsis when shortened.</summary>
    public static string Truncate(string? text, int max)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', max);
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd(',', ';', ':', '.', ' ') + "…";
    }

    /// <summary>
    /// Privacy-friendly embed URL for a YouTube or Vimeo link: youtube-nocookie.com, or Vimeo with
    /// do-not-track. Returns null for anything else, so editors can't embed arbitrary sites.
    /// </summary>
    public static string? VideoEmbedUrl(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        string? id = null;
        if (host is "youtu.be")
        {
            id = uri.AbsolutePath.Trim('/');
        }
        else if (host.EndsWith("youtube.com", StringComparison.Ordinal) || host.EndsWith("youtube-nocookie.com", StringComparison.Ordinal))
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (query.TryGetValue("v", out var v))
            {
                id = v.ToString();
            }
            else
            {
                var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0] is "embed" or "shorts" or "live")
                {
                    id = parts[1];
                }
            }

            if (id is not null && YouTubeId().IsMatch(id))
            {
                return $"https://www.youtube-nocookie.com/embed/{id}?autoplay=1&rel=0";
            }

            return null;
        }
        else if (host.EndsWith("vimeo.com", StringComparison.Ordinal))
        {
            var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(p => p.All(char.IsDigit));
            return last is null ? null : $"https://player.vimeo.com/video/{last}?dnt=1&autoplay=1";
        }

        return id is not null && YouTubeId().IsMatch(id) ? $"https://www.youtube-nocookie.com/embed/{id}?autoplay=1&rel=0" : null;
    }

    /// <summary>Only allow site-relative links or http(s) links in editor-entered button URLs.</summary>
    public static string SafeUrl(string? url, string fallback = "/")
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return fallback;
        }

        url = url.Trim();

        // Browsers treat a backslash like a slash and ignore tabs and line breaks inside a URL, so
        // "/\evil.example", "\\evil.example" and "/<tab>/evil.example" all become protocol-relative.
        // None of these are things an editor needs, so any backslash or control character is refused.
        if (url.Any(ch => ch == '\\' || char.IsControl(ch)))
        {
            return fallback;
        }

        if (url.StartsWith('/'))
        {
            return url.StartsWith("//", StringComparison.Ordinal) ? fallback : url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) ? url : fallback;
    }

    /// <summary>
    /// ISO 8601 duration for a course length such as "2 years", "35 weeks" or "9 months" ("P2Y", "P35W",
    /// "P9M"). Returns null when the text does not start with a number and a unit.
    /// </summary>
    public static string? IsoDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DurationText().Match(text.Trim());
        if (!match.Success || !int.TryParse(match.Groups["n"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToLowerInvariant() switch
        {
            "year" or "yr" => "Y",
            "month" => "M",
            "week" or "wk" => "W",
            "day" => "D",
            _ => null
        };

        return unit is null ? null : $"P{n.ToString(System.Globalization.CultureInfo.InvariantCulture)}{unit}";
    }

    /// <summary>The number in a fee that is just a pound amount ("£1,250" or "£90.50"), otherwise null.</summary>
    public static decimal? PlainPoundAmount(string? fee)
    {
        if (string.IsNullOrWhiteSpace(fee))
        {
            return null;
        }

        var match = PoundAmount().Match(fee.Trim());
        return match.Success && decimal.TryParse(match.Groups["n"].Value.Replace(",", string.Empty, StringComparison.Ordinal), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    [GeneratedRegex(@"^(?<n>\d{1,3})\s*[- ]?\s*(?<unit>year|yr|month|week|wk|day)s?\b", RegexOptions.IgnoreCase)]
    private static partial Regex DurationText();

    [GeneratedRegex(@"^£\s?(?<n>\d{1,3}(,\d{3})*(\.\d{1,2})?|\d+(\.\d{1,2})?)$")]
    private static partial Regex PoundAmount();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex("^[A-Za-z0-9_-]{6,20}$")]
    private static partial Regex YouTubeId();
}
