namespace Chelmsford.Core.Integration;

/// <summary>
/// The website's copy of an enrolled student, synced from the college's student records so the
/// payment journeys can look people up without touching the source system.
/// </summary>
public sealed class SyncedStudent
{
    public string StudentId { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string Email { get; set; } = string.Empty;
    public int CourseId { get; set; }
}

/// <summary>The website's copy of an applicant who has not enrolled yet, keyed by application reference.</summary>
public sealed class SyncedApplication
{
    public string Reference { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int CourseId { get; set; }
}
