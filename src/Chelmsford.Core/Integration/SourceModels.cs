namespace Chelmsford.Core.Integration;

/// <summary>
/// A row from the college's <c>courses</c> table, exactly as stored: raw HTML, 'NULL' strings and all.
/// Column names mirror the college's own schema.
/// </summary>
public sealed record SourceCourse(IReadOnlyDictionary<string, string?> Columns)
{
    public string? this[string column] => Columns.TryGetValue(column, out var value) ? value : null;
}

public sealed record SourceSection(int Id, string? Title);
public sealed record SourceLevel(int Id, string? Title);
public sealed record SourceLink(int CourseId, int TargetId);
public sealed record SourceFunding(int Id, string? Title);

/// <summary>Enrolled students. In production this comes from the student records system.</summary>
public sealed record SourceStudent(string StudentId, string FirstName, string LastName, DateOnly DateOfBirth, string Email, int CourseId);

/// <summary>Applicants who have not enrolled yet, identified by application reference.</summary>
public sealed record SourceApplication(string Reference, string FirstName, string LastName, string Email, int CourseId);

public sealed record SourceSnapshot(
    IReadOnlyList<SourceCourse> Courses,
    IReadOnlyList<SourceSection> Sections,
    IReadOnlyList<SourceLevel> Levels,
    IReadOnlyList<SourceLink> LevelLinks,
    IReadOnlyList<SourceFunding> Funding,
    IReadOnlyList<SourceLink> FundingLinks,
    IReadOnlyList<SourceStudent> Students,
    IReadOnlyList<SourceApplication> Applications);

/// <summary>Reads the college's existing tables through a read-only connection. Never writes.</summary>
public interface ICollegeSourceReader
{
    Task<SourceSnapshot> ReadAsync(CancellationToken cancellationToken = default);
}
