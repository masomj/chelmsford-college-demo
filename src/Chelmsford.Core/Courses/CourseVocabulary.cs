namespace Chelmsford.Core.Courses;

public sealed record LevelInfo(int Order, string Short, string Long);

/// <summary>
/// Turns the college's coded values into plain-English labels that learners use.
/// </summary>
public static class CourseVocabulary
{
    public const string FullTime = "Full-time";
    public const string PartTime = "Part-time";

    public const string Funding16To18 = "Free for 16 to 18 year olds";
    public const string FundingAdult = "Funding for adults";

    /// <summary>Maps a course_levels.L_TITLE to a short label for filters and a long label for course pages.</summary>
    public static LevelInfo Level(string? title)
    {
        var t = (title ?? string.Empty).ToLowerInvariant();
        if (t.Contains("rarpa", StringComparison.Ordinal))
        {
            return new LevelInfo(0, "Supported learning", "Supported learning (non-accredited)");
        }

        if (t.Contains("entry", StringComparison.Ordinal))
        {
            return new LevelInfo(1, "Entry level", "Entry level");
        }

        if (t.Contains("level one", StringComparison.Ordinal) || t.Contains("level 1", StringComparison.Ordinal))
        {
            return new LevelInfo(2, "Level 1", "Foundation (Level 1)");
        }

        if (t.Contains("level two", StringComparison.Ordinal) || t.Contains("level 2", StringComparison.Ordinal))
        {
            return new LevelInfo(3, "Level 2", "Intermediate (Level 2)");
        }

        if (t.Contains("level three", StringComparison.Ordinal) || t.Contains("level 3", StringComparison.Ordinal))
        {
            return new LevelInfo(4, "Level 3", "Advanced (Level 3)");
        }

        if (t.Contains("level four", StringComparison.Ordinal) || t.Contains("level 4", StringComparison.Ordinal))
        {
            return new LevelInfo(5, "Level 4 and above", "Higher (Level 4 and above)");
        }

        return new LevelInfo(9, "Other", string.IsNullOrWhiteSpace(title) ? "Not specified" : title.Trim());
    }

    /// <summary>Plain-English subject name from a course_sections.S_TITLE.</summary>
    public static string Subject(string? sectionTitle)
    {
        var title = (sectionTitle ?? string.Empty).Trim();
        return title switch
        {
            "ICT & Digital Skills" => "Digital and IT",
            "Access to HE Diplomas (Studied Online)" => "Access to Higher Education",
            "Bespoke Provision" or "Foundation Provision" or "Supported Provision" or "INSPIRE" => "Supported learning",
            "" => "Other courses",
            _ => title.Replace(" & ", " and ", StringComparison.Ordinal)
        };
    }

    public static string StudyMode(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "PT" => PartTime,
        "FT" or "" => FullTime,
        var other => other
    };

    /// <summary>Groups the college's funding streams into the two questions learners actually ask.</summary>
    public static IEnumerable<string> FundingGroups(IEnumerable<string> fundingTitles)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in fundingTitles)
        {
            var title = raw.Trim();
            if (title.StartsWith("16-1", StringComparison.Ordinal))
            {
                groups.Add(Funding16To18);
            }
            else if (title.Length > 0 && !title.Equals("Full Cost", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(FundingAdult);
            }
        }

        return groups.Order(StringComparer.Ordinal);
    }
}
