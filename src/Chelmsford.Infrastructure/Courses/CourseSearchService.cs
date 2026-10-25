using Chelmsford.Core.Courses;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Chelmsford.Infrastructure.Courses;

/// <summary>
/// Course search with drill-down facets over the website's synced copy. Only published courses that
/// are still in the source are shown.
/// </summary>
/// <remarks>
/// A college prospectus is a few hundred courses, so the active set is loaded once per search and
/// filtered in memory: facet counts need the result of every "all filters but one" combination, which
/// is simpler and faster here than one database query per facet.
/// </remarks>
public sealed class CourseSearchService(SiteDbContext db) : ICourseSearchService
{
    private static readonly string[] AllModes = [CourseVocabulary.FullTime, CourseVocabulary.PartTime];
    private static readonly string[] AllFundingGroups = [CourseVocabulary.Funding16To18, CourseVocabulary.FundingAdult];

    private enum Facet
    {
        Subject,
        Level,
        Mode,
        Campus,
        Funding
    }

    public async Task<CourseSearchResult> SearchAsync(CourseSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var active = await ActiveCourses().ToListAsync(cancellationToken);
        var words = Words(query.Text);
        var textMatches = active.Where(course => MatchesText(course, words)).ToList();
        var filters = new Filters(query);

        var results = textMatches
            .Where(course => filters.Matches(course, except: null))
            .OrderBy(course => course.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(course => course.LevelOrder)
            .ThenBy(course => course.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(course => course.Id)
            .ToList();

        return new CourseSearchResult(
            results,
            SubjectFacet(textMatches, filters),
            LevelFacet(textMatches, filters),
            FixedFacet(textMatches, filters, Facet.Mode, AllModes, c => [c.StudyMode]),
            FixedFacet(textMatches, filters, Facet.Campus, CampusMapping.All, c => [c.Campus]),
            FixedFacet(textMatches, filters, Facet.Funding, AllFundingGroups, c => c.FundingGroups),
            active.Count);
    }

    public async Task<SyncedCourse?> GetAsync(int courseId, CancellationToken cancellationToken = default) =>
        await ActiveCourses().FirstOrDefaultAsync(course => course.Id == courseId, cancellationToken);

    public async Task<IReadOnlyList<SubjectSummary>> SubjectsAsync(CancellationToken cancellationToken = default)
    {
        var subjects = await ActiveCourses()
            .GroupBy(course => course.Subject)
            .Select(group => new { Subject = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        return subjects
            .OrderBy(s => s.Subject, StringComparer.OrdinalIgnoreCase)
            .Select(s => new SubjectSummary(s.Subject, s.Count))
            .ToList();
    }

    private IQueryable<SyncedCourse> ActiveCourses() =>
        db.SyncedCourses.AsNoTracking().Where(course => course.Published && !course.RemovedFromSource);

    private static string[] Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Every word must appear in the title, heading, subject or qualification.</summary>
    private static bool MatchesText(SyncedCourse course, string[] words)
    {
        if (words.Length == 0)
        {
            return true;
        }

        var haystack = string.Join(' ', course.Title, course.Heading, course.Subject, course.QualificationText ?? string.Empty);
        return words.All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<FacetOption> SubjectFacet(List<SyncedCourse> courses, Filters filters)
    {
        var counts = Count(courses, filters, Facet.Subject, c => [c.Subject]);
        return courses.Select(c => c.Subject)
            .Concat(filters.Subjects)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(subject => subject, StringComparer.OrdinalIgnoreCase)
            .Select(subject => new FacetOption(subject, counts.GetValueOrDefault(subject), filters.Subjects.Contains(subject)))
            .ToList();
    }

    private static IReadOnlyList<FacetOption> LevelFacet(List<SyncedCourse> courses, Filters filters)
    {
        var counts = Count(courses, filters, Facet.Level, c => [c.LevelShort]);
        var order = courses
            .GroupBy(c => c.LevelShort, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(c => c.LevelOrder), StringComparer.OrdinalIgnoreCase);

        return order.Keys
            .Concat(filters.Levels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(level => order.TryGetValue(level, out var o) ? o : int.MaxValue)
            .ThenBy(level => level, StringComparer.OrdinalIgnoreCase)
            .Select(level => new FacetOption(level, counts.GetValueOrDefault(level), filters.Levels.Contains(level)))
            .ToList();
    }

    /// <summary>A facet whose options are always listed, even when no course matches them.</summary>
    private static IReadOnlyList<FacetOption> FixedFacet(
        List<SyncedCourse> courses,
        Filters filters,
        Facet facet,
        IReadOnlyList<string> fixedValues,
        Func<SyncedCourse, IEnumerable<string>> values)
    {
        var counts = Count(courses, filters, facet, values);
        var selected = filters.For(facet);
        var extras = courses.SelectMany(values)
            .Where(value => !fixedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        return fixedValues.Concat(extras)
            .Select(value => new FacetOption(value, counts.GetValueOrDefault(value), selected.Contains(value)))
            .ToList();
    }

    /// <summary>Counts per option over courses matching every filter except this facet's own (drill-down faceting).</summary>
    private static Dictionary<string, int> Count(
        List<SyncedCourse> courses,
        Filters filters,
        Facet facet,
        Func<SyncedCourse, IEnumerable<string>> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var course in courses.Where(c => filters.Matches(c, except: facet)))
        {
            foreach (var value in values(course).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[value] = counts.GetValueOrDefault(value) + 1;
            }
        }

        return counts;
    }

    /// <summary>OR within a facet, AND across facets.</summary>
    private sealed class Filters(CourseSearchQuery query)
    {
        public HashSet<string> Subjects { get; } = ToSet(query.Subjects);
        public HashSet<string> Levels { get; } = ToSet(query.Levels);
        public HashSet<string> Modes { get; } = ToSet(query.Modes);
        public HashSet<string> Campuses { get; } = ToSet(query.Campuses);
        public HashSet<string> Funding { get; } = ToSet(query.Funding);

        public HashSet<string> For(Facet facet) => facet switch
        {
            Facet.Subject => Subjects,
            Facet.Level => Levels,
            Facet.Mode => Modes,
            Facet.Campus => Campuses,
            _ => Funding
        };

        public bool Matches(SyncedCourse course, Facet? except) =>
            (except == Facet.Subject || Subjects.Count == 0 || Subjects.Contains(course.Subject))
            && (except == Facet.Level || Levels.Count == 0 || Levels.Contains(course.LevelShort))
            && (except == Facet.Mode || Modes.Count == 0 || Modes.Contains(course.StudyMode))
            && (except == Facet.Campus || Campuses.Count == 0 || Campuses.Contains(course.Campus))
            && (except == Facet.Funding || Funding.Count == 0 || course.FundingGroups.Any(Funding.Contains));

        private static HashSet<string> ToSet(IEnumerable<string> values) =>
            new(values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
    }
}
