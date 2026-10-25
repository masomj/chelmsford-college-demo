using Chelmsford.Core.Integration;

namespace Chelmsford.Infrastructure.Integration;

/// <summary>
/// The college's lookup tables (sections, levels, funding and their link tables), indexed for
/// mapping courses. Levels and funding are only reachable through the link tables.
/// </summary>
public sealed class CourseLookups
{
    private static readonly IReadOnlyList<int> NoIds = [];

    private readonly Dictionary<int, string?> _sections;
    private readonly Dictionary<int, string?> _levels;
    private readonly Dictionary<int, string?> _funding;
    private readonly Dictionary<int, List<int>> _levelLinks;
    private readonly Dictionary<int, List<int>> _fundingLinks;

    public CourseLookups(
        IEnumerable<SourceSection> sections,
        IEnumerable<SourceLevel> levels,
        IEnumerable<SourceLink> levelLinks,
        IEnumerable<SourceFunding> funding,
        IEnumerable<SourceLink> fundingLinks)
    {
        _sections = ToTitleMap(sections.Select(s => (s.Id, s.Title)));
        _levels = ToTitleMap(levels.Select(l => (l.Id, l.Title)));
        _funding = ToTitleMap(funding.Select(f => (f.Id, f.Title)));
        _levelLinks = ToLinkMap(levelLinks, _levels);
        _fundingLinks = ToLinkMap(fundingLinks, _funding);
    }

    public static CourseLookups From(SourceSnapshot snapshot) =>
        new(snapshot.Sections, snapshot.Levels, snapshot.LevelLinks, snapshot.Funding, snapshot.FundingLinks);

    public string? SectionTitle(int sectionId) => _sections.GetValueOrDefault(sectionId);

    /// <summary>IDs of the levels linked to a course, ascending and without duplicates.</summary>
    public IReadOnlyList<int> LevelIds(int courseId) => _levelLinks.TryGetValue(courseId, out var ids) ? ids : NoIds;

    /// <summary>IDs of the funding streams linked to a course, ascending and without duplicates.</summary>
    public IReadOnlyList<int> FundingIds(int courseId) => _fundingLinks.TryGetValue(courseId, out var ids) ? ids : NoIds;

    public string? LevelTitle(int levelId) => _levels.GetValueOrDefault(levelId);

    public string? FundingTitle(int fundingId) => _funding.GetValueOrDefault(fundingId);

    private static Dictionary<int, string?> ToTitleMap(IEnumerable<(int Id, string? Title)> rows)
    {
        var map = new Dictionary<int, string?>();
        foreach (var (id, title) in rows)
        {
            map.TryAdd(id, title);
        }

        return map;
    }

    /// <summary>Groups links by course, ignoring links to rows that don't exist.</summary>
    private static Dictionary<int, List<int>> ToLinkMap(IEnumerable<SourceLink> links, Dictionary<int, string?> targets) =>
        links.Where(link => targets.ContainsKey(link.TargetId))
            .GroupBy(link => link.CourseId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.TargetId).Distinct().Order().ToList());
}
