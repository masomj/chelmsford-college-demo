using Chelmsford.Core.Courses;
using Chelmsford.Infrastructure.Courses;
using Chelmsford.Tests.Fakes;
using static Chelmsford.Tests.Fakes.TestCourses;

namespace Chelmsford.Tests;

public sealed class CourseSearchServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public CourseSearchServiceTests()
    {
        using var db = _database.CreateContext();
        db.SyncedCourses.AddRange(
            Course(1, "Carpentry and Joinery", "Construction", 3, "Level 2"),
            Course(2, "Advanced Carpentry", "Construction", 4, "Level 3", funding: CourseVocabulary.Funding16To18 + "|" + CourseVocabulary.FundingAdult),
            Course(3, "Fashion and Textiles", "Creative Design", 4, "Level 3", qualification: "Example Extended Diploma"),
            Course(4, "Digital Skills for Work", "Digital and IT", 1, "Entry level", mode: CourseVocabulary.PartTime, funding: CourseVocabulary.FundingAdult),
            Course(5, "Unpublished Course", "Construction", 3, "Level 2", published: false),
            Course(6, "Removed Course", "Construction", 3, "Level 2", removed: true));
        db.SaveChanges();
    }

    public void Dispose() => _database.Dispose();

    private async Task<CourseSearchResult> SearchAsync(CourseSearchQuery query)
    {
        await using var db = _database.CreateContext();
        return await new CourseSearchService(db).SearchAsync(query);
    }

    [Fact]
    public async Task Only_active_courses_are_returned_in_subject_level_title_order()
    {
        var result = await SearchAsync(new CourseSearchQuery());

        Assert.Equal([1, 2, 3, 4], result.Courses.Select(c => c.Id));
        Assert.Equal(4, result.TotalCourses);
    }

    [Fact]
    public async Task Text_search_needs_every_word_and_ignores_case()
    {
        var result = await SearchAsync(new CourseSearchQuery { Text = "CARPENTRY advanced" });

        Assert.Equal([2], result.Courses.Select(c => c.Id));
    }

    [Fact]
    public async Task Text_search_matches_subject_and_qualification()
    {
        Assert.Equal([1, 2], (await SearchAsync(new CourseSearchQuery { Text = "construction" })).Courses.Select(c => c.Id));
        Assert.Equal([3], (await SearchAsync(new CourseSearchQuery { Text = "example diploma" })).Courses.Select(c => c.Id));
    }

    [Fact]
    public async Task Filters_are_or_within_a_facet_and_and_across_facets()
    {
        var result = await SearchAsync(new CourseSearchQuery
        {
            Subjects = ["Construction", "Creative Design"],
            Levels = ["Level 3"]
        });

        Assert.Equal([2, 3], result.Courses.Select(c => c.Id));
    }

    [Fact]
    public async Task Facet_counts_ignore_their_own_selection()
    {
        var result = await SearchAsync(new CourseSearchQuery { Subjects = ["Construction"] });

        // Subject counts are over everything (no other filter is set), so other subjects stay selectable.
        Assert.Equal(2, Count(result.Subjects, "Construction"));
        Assert.Equal(1, Count(result.Subjects, "Creative Design"));
        Assert.True(result.Subjects.Single(s => s.Value == "Construction").Selected);

        // Level counts are within the selected subject.
        Assert.Equal(1, Count(result.Levels, "Level 2"));
        Assert.Equal(1, Count(result.Levels, "Level 3"));
        Assert.Equal(0, Count(result.Levels, "Entry level"));
    }

    [Fact]
    public async Task Levels_are_ordered_by_level_and_subjects_alphabetically()
    {
        var result = await SearchAsync(new CourseSearchQuery());

        Assert.Equal(["Entry level", "Level 2", "Level 3"], result.Levels.Select(l => l.Value));
        Assert.Equal(["Construction", "Creative Design", "Digital and IT"], result.Subjects.Select(s => s.Value));
    }

    [Fact]
    public async Task Modes_campuses_and_funding_always_list_every_option()
    {
        var result = await SearchAsync(new CourseSearchQuery { Text = "fashion" });

        Assert.Equal([CourseVocabulary.FullTime, CourseVocabulary.PartTime], result.Modes.Select(m => m.Value));
        Assert.Equal(0, Count(result.Modes, CourseVocabulary.PartTime));
        Assert.Equal(CampusMapping.All, result.Campuses.Select(c => c.Value));
        Assert.Equal(0, Count(result.Campuses, CampusMapping.PrincesRoad));
        Assert.Equal([CourseVocabulary.Funding16To18, CourseVocabulary.FundingAdult], result.Funding.Select(f => f.Value));
        Assert.Equal(0, Count(result.Funding, CourseVocabulary.FundingAdult));
    }

    [Fact]
    public async Task Funding_filter_matches_any_of_a_courses_groups()
    {
        var result = await SearchAsync(new CourseSearchQuery { Funding = [CourseVocabulary.FundingAdult] });

        Assert.Equal([2, 4], result.Courses.Select(c => c.Id));
        Assert.Equal(3, Count(result.Funding, CourseVocabulary.Funding16To18));
    }

    [Fact]
    public async Task Get_and_subjects_only_see_active_courses()
    {
        await using var db = _database.CreateContext();
        var service = new CourseSearchService(db);

        Assert.NotNull(await service.GetAsync(1));
        Assert.Null(await service.GetAsync(5));
        Assert.Null(await service.GetAsync(6));
        Assert.Equal(
            [new SubjectSummary("Construction", 2), new SubjectSummary("Creative Design", 1), new SubjectSummary("Digital and IT", 1)],
            await service.SubjectsAsync());
    }

    private static int Count(IEnumerable<FacetOption> options, string value) =>
        options.Single(o => o.Value == value).Count;
}
