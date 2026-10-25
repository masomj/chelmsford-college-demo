namespace Chelmsford.Core.Courses;

/// <summary>
/// The website's own copy of a course, kept in sync from the college's tables by the
/// integration layer. Visitors are always served from this copy, never from the source.
/// </summary>
public sealed class SyncedCourse
{
    /// <summary>The college's course ID (courses.C_ID).</summary>
    public int Id { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Heading { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? SeoTitle { get; set; }
    public string? MetaDescription { get; set; }

    public int SectionId { get; set; }
    public string Subject { get; set; } = string.Empty;

    public int LevelOrder { get; set; }
    public string LevelShort { get; set; } = string.Empty;
    public string LevelLong { get; set; } = string.Empty;

    public string StudyMode { get; set; } = CourseVocabulary.FullTime;
    public string Campus { get; set; } = string.Empty;
    public string? Duration { get; set; }
    public string? DaysText { get; set; }
    public DateOnly? StartDate { get; set; }
    public string? StartText { get; set; }

    public string? QualificationText { get; set; }
    public string? CourseCode { get; set; }

    /// <summary>Pipe-separated funding groups, for filtering.</summary>
    public string Funding { get; set; } = string.Empty;

    public string? OverviewHtml { get; set; }
    public string? StudyHtml { get; set; }
    public string? AssessmentHtml { get; set; }
    public string? EntryHtml { get; set; }
    public string? FeesText { get; set; }
    public string? FeesHtml { get; set; }
    public string? AdditionalFeesHtml { get; set; }
    public string? ProgressionHtml { get; set; }
    public string? ContactEmail { get; set; }

    public bool Published { get; set; }
    public bool RemovedFromSource { get; set; }

    /// <summary>Hash of the source row, so unchanged courses are skipped on each sync.</summary>
    public string SourceHash { get; set; } = string.Empty;
    public DateTime SyncedUtc { get; set; }

    public IReadOnlyList<string> FundingGroups =>
        Funding.Length == 0 ? [] : Funding.Split('|', StringSplitOptions.RemoveEmptyEntries);

    public string StartLabel =>
        StartDate is { } date ? date.ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB")) : StartText ?? "Contact us";
}
