using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Infrastructure.Integration;

namespace Chelmsford.Tests;

public sealed class CourseMapperTests
{
    private readonly CourseMapper _mapper = new(new HtmlCleaner());

    private static Dictionary<string, string?> SampleColumns() => new()
    {
        ["C_ID"] = "374",
        ["C_TITLE"] = " Example Level 3 Diploma in Garment Studies: Pattern Making ",
        ["C_SECTION"] = "48",
        ["C_MODE"] = "FT",
        ["C_DURATION"] = "2 years ",
        ["C_DAYS"] = "NULL",
        ["C_QUALIFICATION"] = "<p>Example Level 3 Diploma in Garment Studies</p>_x000D_\n",
        ["C_COURSECODE"] = "NULL",
        ["C_STARTDATE"] = "2026-09-01 00:00:00",
        ["C_STUDYOVERVIEW"] = "<p style=\"text-align: justify\">Sample study overview.</p>_x000D_\n",
        ["C_EMPLOYABILITY"] = "<p>Sample employability text.</p>",
        ["C_ELIGIBILITY"] = "<p>4 GCSEs grades 9-4</p>_x000D_\n",
        ["C_ELIGIBILITYSPECIFIC"] = "<p>Portfolio interview</p>",
        ["C_ASSESSMENT"] = "<p>Internally assessed course work and portfolio&nbsp;</p>_x000D_\n",
        ["C_FEES"] = "<p>&pound;150</p>_x000D_\n",
        ["C_ADDITIONALFEES"] = "NULL",
        ["C_CONTACT"] = "<p><a href=\"mailto:info@example.ac.uk\">c</a>ourses@example.ac.uk&nbsp;</p>_x000D_\n",
        ["C_METADESC"] = "NULL",
        ["C_SEOTITLE"] = "Example Level 3 Diploma in Garment Studies",
        ["C_SEOH1"] = "Example Level 3 Extended Diploma in Garments & Patterns",
        ["C_COURSEFORME"] = "<p>Are you curious about sewing, pattern cutting or costume? This made-up sample blurb is deliberately long enough to be cut at a word boundary when it becomes a meta description, so it keeps going with extra filler words.</p>",
        ["C_DELETED"] = "NULL",
        ["C_PUBLISHED"] = "1"
    };

    private static SourceSnapshot Snapshot(params Dictionary<string, string?>[] courses) => new(
        courses.Select(c => new SourceCourse(c)).ToList(),
        [new SourceSection(48, "Creative Design"), new SourceSection(10, "Construction")],
        [new SourceLevel(3, "Intermediate (Level Two)"), new SourceLevel(4, "Advanced (Level Three)")],
        [new SourceLink(374, 4), new SourceLink(374, 3), new SourceLink(374, 99)],
        [new SourceFunding(1, "16-18"), new SourceFunding(7, "Full Cost"), new SourceFunding(13, "19+")],
        [new SourceLink(374, 1), new SourceLink(374, 7)],
        [],
        []);

    [Fact]
    public void Maps_a_raw_course_into_the_website_copy()
    {
        var course = Assert.Single(_mapper.MapAll(Snapshot(SampleColumns())));

        Assert.Equal(374, course.Id);
        Assert.Equal("Example Level 3 Diploma in Garment Studies: Pattern Making", course.Title);
        Assert.Equal("Example Level 3 Extended Diploma in Garments & Patterns", course.Heading);
        Assert.Equal("example-level-3-extended-diploma-in-garments-patterns", course.Slug);
        Assert.Equal("Creative Design", course.Subject);
        Assert.Equal(CampusMapping.MoulshamStreet, course.Campus);
        Assert.Equal(CourseVocabulary.FullTime, course.StudyMode);
        Assert.Equal("2 years", course.Duration);
        Assert.Null(course.DaysText);
        Assert.Equal(new DateOnly(2026, 9, 1), course.StartDate);
        Assert.Null(course.StartText);
        Assert.Null(course.CourseCode);
        Assert.Equal("£150", course.FeesText);
        Assert.Equal("<p>£150</p>", course.FeesHtml);
        Assert.Null(course.AdditionalFeesHtml);
        Assert.Equal("<p>Sample study overview.</p>", course.StudyHtml);
        Assert.Equal("<p>4 GCSEs grades 9-4</p><p>Portfolio interview</p>", course.EntryHtml);
        Assert.Equal("courses@example.ac.uk", course.ContactEmail);
        Assert.True(course.Published);
        Assert.Equal(64, course.SourceHash.Length);
    }

    [Fact]
    public void Level_is_the_lowest_linked_level_and_unknown_links_are_ignored()
    {
        var course = Assert.Single(_mapper.MapAll(Snapshot(SampleColumns())));

        Assert.Equal(3, course.LevelOrder);
        Assert.Equal("Level 2", course.LevelShort);
    }

    [Fact]
    public void Funding_groups_come_from_the_link_table()
    {
        var course = Assert.Single(_mapper.MapAll(Snapshot(SampleColumns())));

        Assert.Equal(CourseVocabulary.Funding16To18, course.Funding);
    }

    [Fact]
    public void Meta_description_falls_back_to_the_overview_cut_at_a_word()
    {
        var course = Assert.Single(_mapper.MapAll(Snapshot(SampleColumns())));

        Assert.NotNull(course.MetaDescription);
        Assert.True(course.MetaDescription!.Length <= 156);
        Assert.EndsWith("…", course.MetaDescription, StringComparison.Ordinal);
        Assert.StartsWith("Are you curious about sewing", course.MetaDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", course.MetaDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void Free_text_start_dates_are_kept_as_text()
    {
        var columns = SampleColumns();
        columns["C_STARTDATE"] = "October 2026, January 2027 and April 2027.";

        var course = Assert.Single(_mapper.MapAll(Snapshot(columns)));

        Assert.Null(course.StartDate);
        Assert.Equal("October 2026, January 2027 and April 2027.", course.StartText);
    }

    [Fact]
    public void Deleted_or_unpublished_courses_are_not_published()
    {
        var deleted = SampleColumns();
        deleted["C_DELETED"] = "2025-01-01 00:00:00";
        var unpublished = SampleColumns();
        unpublished["C_ID"] = "375";
        unpublished["C_PUBLISHED"] = "0";

        var courses = _mapper.MapAll(Snapshot(deleted, unpublished));

        Assert.All(courses, c => Assert.False(c.Published));
    }

    [Fact]
    public void Duplicate_headings_get_unique_slugs()
    {
        var second = SampleColumns();
        second["C_ID"] = "380";

        var courses = _mapper.MapAll(Snapshot(SampleColumns(), second));

        Assert.Equal("example-level-3-extended-diploma-in-garments-patterns", courses.Single(c => c.Id == 374).Slug);
        Assert.Equal("example-level-3-extended-diploma-in-garments-patterns-380", courses.Single(c => c.Id == 380).Slug);
    }

    [Fact]
    public void Hash_is_stable_and_changes_with_the_source()
    {
        var first = Assert.Single(_mapper.MapAll(Snapshot(SampleColumns()))).SourceHash;
        var again = Assert.Single(_mapper.MapAll(Snapshot(SampleColumns()))).SourceHash;
        var changedColumns = SampleColumns();
        changedColumns["C_FEES"] = "<p>&pound;175</p>";
        var changed = Assert.Single(_mapper.MapAll(Snapshot(changedColumns))).SourceHash;

        Assert.Equal(first, again);
        Assert.NotEqual(first, changed);
    }

    [Fact]
    public void Heading_falls_back_to_title()
    {
        var columns = SampleColumns();
        columns["C_SEOH1"] = "NULL";

        var course = Assert.Single(_mapper.MapAll(Snapshot(columns)));

        Assert.Equal(course.Title, course.Heading);
    }
}
