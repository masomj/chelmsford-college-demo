namespace Chelmsford.Core.Courses;

public sealed record CourseSearchQuery
{
    public string? Text { get; init; }
    public IReadOnlyCollection<string> Subjects { get; init; } = [];
    public IReadOnlyCollection<string> Levels { get; init; } = [];
    public IReadOnlyCollection<string> Modes { get; init; } = [];
    public IReadOnlyCollection<string> Campuses { get; init; } = [];
    public IReadOnlyCollection<string> Funding { get; init; } = [];

    public int ActiveFilterCount => Subjects.Count + Levels.Count + Modes.Count + Campuses.Count + Funding.Count;
}

public sealed record FacetOption(string Value, int Count, bool Selected);

public sealed record CourseSearchResult(
    IReadOnlyList<SyncedCourse> Courses,
    IReadOnlyList<FacetOption> Subjects,
    IReadOnlyList<FacetOption> Levels,
    IReadOnlyList<FacetOption> Modes,
    IReadOnlyList<FacetOption> Campuses,
    IReadOnlyList<FacetOption> Funding,
    int TotalCourses);

public interface ICourseSearchService
{
    Task<CourseSearchResult> SearchAsync(CourseSearchQuery query, CancellationToken cancellationToken = default);
    Task<SyncedCourse?> GetAsync(int courseId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SubjectSummary>> SubjectsAsync(CancellationToken cancellationToken = default);
}

public sealed record SubjectSummary(string Subject, int CourseCount);
