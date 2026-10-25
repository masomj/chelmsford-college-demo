using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chelmsford.Core.Common;
using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure.Source;

namespace Chelmsford.Infrastructure.Integration;

/// <summary>Turns the college's raw course rows into the website's clean copy.</summary>
public interface ICourseMapper
{
    /// <summary>Maps every course in the snapshot, giving each a unique slug.</summary>
    IReadOnlyList<SyncedCourse> MapAll(SourceSnapshot snapshot);

    /// <summary>Maps one course. The slug is not de-duplicated against other courses.</summary>
    SyncedCourse Map(SourceCourse course, CourseLookups lookups);
}

public sealed class CourseMapper(IHtmlCleaner htmlCleaner) : ICourseMapper
{
    /// <summary>Bump when the mapping or cleaning rules change, so every course is re-synced.</summary>
    internal const string MappingVersion = "1";

    private const int MetaDescriptionLength = 155;

    private static readonly string[] StartDateFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFF",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd"
    ];

    public IReadOnlyList<SyncedCourse> MapAll(SourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var lookups = CourseLookups.From(snapshot);
        var mapped = snapshot.Courses
            .Select(course => Map(course, lookups))
            .GroupBy(course => course.Id)
            .Select(group => group.First())
            .OrderBy(course => course.Id)
            .ToList();

        // Lowest course ID keeps the plain slug; later courses with the same heading get "-{id}".
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var course in mapped)
        {
            if (!used.Add(course.Slug))
            {
                course.Slug = $"{course.Slug}-{course.Id.ToString(CultureInfo.InvariantCulture)}";
                used.Add(course.Slug);
            }
        }

        return mapped;
    }

    public SyncedCourse Map(SourceCourse course, CourseLookups lookups)
    {
        ArgumentNullException.ThrowIfNull(course);
        ArgumentNullException.ThrowIfNull(lookups);

        var id = SourceTables.ParseId(course["C_ID"])
                 ?? throw new ArgumentException("The course has no C_ID.", nameof(course));

        var title = Plain(course["C_TITLE"]) ?? $"Course {id.ToString(CultureInfo.InvariantCulture)}";
        var heading = Plain(course["C_SEOH1"]) ?? title;
        var sectionId = SourceTables.ParseId(course["C_SECTION"]) ?? 0;
        var subject = CourseVocabulary.Subject(lookups.SectionTitle(sectionId));
        var level = LowestLevel(lookups.LevelIds(id).Select(lookups.LevelTitle));
        var fundingIds = lookups.FundingIds(id);
        var fundingTitles = fundingIds.Select(lookups.FundingTitle).OfType<string>();
        var start = ParseStart(course["C_STARTDATE"]);
        var overviewHtml = htmlCleaner.Clean(course["C_COURSEFORME"]);
        var slug = SourceText.Slug(heading);

        return new SyncedCourse
        {
            Id = id,
            Title = title,
            Heading = heading,
            Slug = slug.Length > 0 ? slug : $"course-{id.ToString(CultureInfo.InvariantCulture)}",
            SeoTitle = Plain(course["C_SEOTITLE"]),
            MetaDescription = Plain(course["C_METADESC"]) ?? Summarise(Plain(course["C_COURSEFORME"]), MetaDescriptionLength),
            SectionId = sectionId,
            Subject = subject,
            LevelOrder = level.Order,
            LevelShort = level.Short,
            LevelLong = level.Long,
            StudyMode = CourseVocabulary.StudyMode(SourceText.Clean(course["C_MODE"])),
            Campus = CampusMapping.ForSubject(subject),
            Duration = Plain(course["C_DURATION"]),
            DaysText = Plain(course["C_DAYS"]),
            StartDate = start.Date,
            StartText = start.Text,
            QualificationText = Plain(course["C_QUALIFICATION"]),
            CourseCode = SourceText.Clean(course["C_COURSECODE"]),
            Funding = string.Join('|', CourseVocabulary.FundingGroups(fundingTitles)),
            OverviewHtml = overviewHtml,
            StudyHtml = htmlCleaner.Clean(course["C_STUDYOVERVIEW"]),
            AssessmentHtml = htmlCleaner.Clean(course["C_ASSESSMENT"]),
            EntryHtml = Join(htmlCleaner.Clean(course["C_ELIGIBILITY"]), htmlCleaner.Clean(course["C_ELIGIBILITYSPECIFIC"])),
            FeesText = Plain(course["C_FEES"]),
            FeesHtml = htmlCleaner.Clean(course["C_FEES"]),
            AdditionalFeesHtml = htmlCleaner.Clean(course["C_ADDITIONALFEES"]),
            ProgressionHtml = htmlCleaner.Clean(course["C_EMPLOYABILITY"]),
            ContactEmail = SourceText.FirstEmail(course["C_CONTACT"]),
            Published = SourceTables.ParseId(course["C_PUBLISHED"]) == 1 && SourceText.Clean(course["C_DELETED"]) is null,
            SourceHash = Hash(course, lookups, id, sectionId, fundingIds)
        };
    }

    /// <summary>Clean plain text, or null when the value is missing.</summary>
    private static string? Plain(string? raw)
    {
        var text = SourceText.PlainText(SourceText.Clean(raw));
        return text.Length == 0 ? null : text;
    }

    private static LevelInfo LowestLevel(IEnumerable<string?> titles)
    {
        LevelInfo? lowest = null;
        foreach (var title in titles)
        {
            var level = CourseVocabulary.Level(title);
            if (lowest is null || level.Order < lowest.Order)
            {
                lowest = level;
            }
        }

        return lowest ?? CourseVocabulary.Level(null);
    }

    private static (DateOnly? Date, string? Text) ParseStart(string? raw)
    {
        var value = SourceText.Clean(raw);
        if (value is null)
        {
            return (null, null);
        }

        if (DateTime.TryParseExact(value, StartDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out var date)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out date) && LooksLikeIsoDate(value))
        {
            return (DateOnly.FromDateTime(date), null);
        }

        return (null, Plain(value));
    }

    private static bool LooksLikeIsoDate(string value) =>
        value.Length >= 10 && char.IsAsciiDigit(value[0]) && value[4] == '-' && value[7] == '-';

    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            (not null, null) => first,
            (null, not null) => second,
            _ => first + second
        };

    /// <summary>The first <paramref name="maxLength"/> characters or so, cut at a word boundary.</summary>
    internal static string? Summarise(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', maxLength);
        var summary = (cut > maxLength / 2 ? text[..cut] : text[..maxLength]).TrimEnd(' ', ',', ';', ':', '-', '–', '.');
        return summary + "…";
    }

    /// <summary>
    /// SHA-256 over every source column (name and raw value, in name order), the linked level and
    /// funding IDs, and the lookup titles those links resolve to, so any change upstream is detected.
    /// </summary>
    private static string Hash(SourceCourse course, CourseLookups lookups, int id, int sectionId, IReadOnlyList<int> fundingIds)
    {
        var builder = new StringBuilder();
        builder.Append("mapping=").Append(MappingVersion).Append('\u001F');
        foreach (var (name, value) in course.Columns.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(name).Append('=').Append(value is null ? "\u0000" : value).Append('\u001F');
        }

        builder.Append("section=").Append(lookups.SectionTitle(sectionId)).Append('\u001F');
        foreach (var levelId in lookups.LevelIds(id))
        {
            builder.Append("level=").Append(levelId.ToString(CultureInfo.InvariantCulture)).Append(':').Append(lookups.LevelTitle(levelId)).Append('\u001F');
        }

        foreach (var fundingId in fundingIds)
        {
            builder.Append("funding=").Append(fundingId.ToString(CultureInfo.InvariantCulture)).Append(':').Append(lookups.FundingTitle(fundingId)).Append('\u001F');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
