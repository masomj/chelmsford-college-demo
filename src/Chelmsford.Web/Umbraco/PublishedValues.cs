using System.Globalization;
using System.Text.Json;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace Chelmsford.Web.Umbraco;

/// <summary>
/// Reads property values defensively. Seeded data types can fall back to a plain textstring if a
/// configured one could not be created, so every reader copes with the raw stored value too.
/// </summary>
public static class PublishedValues
{
    public static string? Text(this IPublishedContent content, string alias)
    {
        var value = content.Value(alias);
        var text = value switch
        {
            null => null,
            string s => s,
            IHtmlEncodedString html => html.ToHtmlString(),
            IEnumerable<string> list => list.FirstOrDefault(),
            _ => value.ToString()
        };

        if (text is not null && text.StartsWith("[\"", StringComparison.Ordinal))
        {
            // A dropdown stored as JSON but read without its value converter.
            try
            {
                text = JsonSerializer.Deserialize<string[]>(text)?.FirstOrDefault();
            }
            catch (JsonException)
            {
            }
        }

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static string? Html(this IPublishedContent content, string alias)
    {
        var value = content.Value(alias);
        var html = value switch
        {
            null => null,
            IHtmlEncodedString encoded => encoded.ToHtmlString(),
            _ => value.ToString()
        };
        return string.IsNullOrWhiteSpace(html) ? null : html;
    }

    public static int? Int(this IPublishedContent content, string alias)
    {
        if (!content.HasValue(alias))
        {
            return null;
        }

        return content.Value(alias) switch
        {
            int i => i,
            long l => (int)l,
            decimal d => (int)d,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    public static decimal? Decimal(this IPublishedContent content, string alias)
    {
        if (!content.HasValue(alias))
        {
            return null;
        }

        return content.Value(alias) switch
        {
            decimal d => d,
            double dbl => (decimal)dbl,
            int i => i,
            string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    public static bool Bool(this IPublishedContent content, string alias) =>
        content.Value(alias) switch
        {
            bool b => b,
            int i => i != 0,
            string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    /// <summary>A date and time as entered by the editor (Europe/London wall-clock time).</summary>
    public static DateTime? DateTime(this IPublishedContent content, string alias)
    {
        if (!content.HasValue(alias))
        {
            return null;
        }

        var value = content.Value(alias);
        DateTime? result = value switch
        {
            System.DateTime dt => dt,
            DateTimeOffset dto => dto.DateTime,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue),
            string s => ParseStored(s),
            _ => ParseStored(content.GetProperty(alias)?.GetSourceValue()?.ToString())
        };

        return result is { } r && r > System.DateTime.MinValue.AddDays(1) ? System.DateTime.SpecifyKind(r, DateTimeKind.Unspecified) : null;
    }

    /// <summary>Parses either a plain date string or the JSON shape used by the newer date-time editors.</summary>
    public static DateTime? ParseStored(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        stored = stored.Trim();
        if (stored.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(stored);
                if (doc.RootElement.TryGetProperty("date", out var date) && date.ValueKind == JsonValueKind.String)
                {
                    stored = date.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (DateTimeOffset.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && stored.Length > 19)
        {
            return dto.DateTime;
        }

        return System.DateTime.TryParse(stored, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
    }

    /// <summary>URL of a picked media item, or null.</summary>
    public static (string Url, string Alt)? Media(this IPublishedContent content, string alias)
    {
        var value = content.Value(alias);
        var media = value switch
        {
            IPublishedContent single => single,
            IEnumerable<IPublishedContent> many => many.FirstOrDefault(),
            _ => null
        };

        if (media is null)
        {
            return null;
        }

        var url = media.Url();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var alt = media.HasProperty("altText") ? media.Value<string>("altText") : null;
        return (url, alt ?? string.Empty);
    }

    /// <summary>Keys from a content picker's stored value ("umb://document/{guid}", comma-separated).</summary>
    public static IReadOnlyList<Guid> PickedKeys(this IPublishedContent content, string alias)
    {
        var value = content.Value(alias);
        if (value is IEnumerable<IPublishedContent> picked)
        {
            return picked.Select(p => p.Key).ToList();
        }

        if (value is IPublishedContent one)
        {
            return [one.Key];
        }

        var raw = content.GetProperty(alias)?.GetSourceValue()?.ToString();
        return ParseUdis(raw);
    }

    public static IReadOnlyList<Guid> ParseUdis(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var keys = new List<Guid>();
        foreach (var part in raw.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = part.Contains('/') ? part[(part.LastIndexOf('/') + 1)..] : part;
            if (Guid.TryParse(id, out var key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    public static string Udi(Guid key) => "umb://document/" + key.ToString("N");
}
