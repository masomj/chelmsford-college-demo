using System.Globalization;
using Chelmsford.Core.Common;

namespace Chelmsford.Infrastructure.Source;

/// <summary>The college's table and column names, exactly as they appear in their database.</summary>
internal static class SourceTables
{
    public const string Courses = "courses";
    public const string Sections = "course_sections";
    public const string Levels = "course_levels";
    public const string LevelLinks = "course_level_link";
    public const string Funding = "course_funding";
    public const string FundingLinks = "course_fund_link";
    public const string Students = "students";
    public const string Applications = "applications";

    /// <summary>
    /// Course tables copied from the college's spreadsheet export, with the minimum columns the
    /// reader needs if a JSON file is missing (so the site can still start with no courses).
    /// </summary>
    public static readonly IReadOnlyList<(string Table, string[] RequiredColumns)> CourseTables =
    [
        (Courses, ["C_ID", "C_TITLE", "C_PUBLISHED", "C_DELETED"]),
        (Sections, ["S_ID", "S_TITLE"]),
        (Levels, ["L_ID", "L_TITLE"]),
        (LevelLinks, ["CL_ID", "CL_COURSEID", "CL_LEVELID"]),
        (Funding, ["F_ID", "F_TITLE"]),
        (FundingLinks, ["CF_ID", "CF_COURSEID", "CF_FUNDID"])
    ];

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// Parses an ID column. Source IDs are stored as text and may arrive as "374", "374.0" or 'NULL'.
    /// </summary>
    public static int? ParseId(string? raw)
    {
        var value = SourceText.Clean(raw);
        if (value is null)
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            return id;
        }

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
               && number == decimal.Truncate(number) && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : null;
    }
}
