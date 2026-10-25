using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Chelmsford.Core.Common;

/// <summary>
/// Normalises raw values from the college's existing tables. The source data stores
/// empty values as the text 'NULL', carries Excel/SQL carriage-return artefacts (_x000D_)
/// and pads values with non-breaking spaces.
/// </summary>
public static partial class SourceText
{
    /// <summary>Returns null for missing values ('NULL', empty, whitespace, &amp;nbsp;), otherwise the trimmed value.</summary>
    public static string? Clean(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var value = raw.Replace("_x000D_", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        if (value.Length == 0 || string.Equals(value, "NULL", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return PlainText(value).Length == 0 && !value.Contains("<img", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    /// <summary>Strips tags and decodes entities, collapsing whitespace.</summary>
    public static string PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = TagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    /// <summary>Finds the first email address in a value, ignoring any broken markup around it.</summary>
    public static string? FirstEmail(string? raw)
    {
        var text = PlainText(Clean(raw)).Replace(" ", string.Empty, StringComparison.Ordinal);
        var match = EmailRegex().Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    public static int? ParseInt(string? raw) =>
        int.TryParse(Clean(raw), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>Lower-case, hyphenated URL slug.</summary>
    public static string Slug(string value)
    {
        var normalised = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        foreach (var c in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }

        return MultiHyphenRegex().Replace(builder.ToString(), "-").Trim('-');
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex("-{2,}")]
    private static partial Regex MultiHyphenRegex();
}
