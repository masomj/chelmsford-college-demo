using Chelmsford.Core.Courses;

namespace Chelmsford.Tests;

public sealed class CourseVocabularyTests
{
    [Theory]
    [InlineData("Pre-Entry/Entry Level", 1, "Entry level")]
    [InlineData("Entry Level / Level One", 1, "Entry level")]
    [InlineData("Foundation (Level One)", 2, "Level 1")]
    [InlineData("Intermediate (Level Two)", 3, "Level 2")]
    [InlineData("Advanced (Level Three)", 4, "Level 3")]
    [InlineData("Higher (Level 4 and above)", 5, "Level 4 and above")]
    [InlineData("Higher (Level Four and above)", 5, "Level 4 and above")]
    [InlineData("RARPA (Recognising & Recording Progress & Achievement)", 0, "Supported learning")]
    [InlineData("Various", 9, "Other")]
    [InlineData(null, 9, "Other")]
    public void Level_maps_college_titles_to_plain_labels(string? title, int order, string shortLabel)
    {
        var level = CourseVocabulary.Level(title);

        Assert.Equal(order, level.Order);
        Assert.Equal(shortLabel, level.Short);
    }

    [Theory]
    [InlineData("ICT & Digital Skills", "Digital and IT")]
    [InlineData("Hair & Beauty", "Hair and Beauty")]
    [InlineData("Supported Provision", "Supported learning")]
    [InlineData("  Science ", "Science")]
    [InlineData(null, "Other courses")]
    public void Subject_uses_plain_english(string? section, string expected) =>
        Assert.Equal(expected, CourseVocabulary.Subject(section));

    [Theory]
    [InlineData("FT", CourseVocabulary.FullTime)]
    [InlineData(" pt ", CourseVocabulary.PartTime)]
    [InlineData(null, CourseVocabulary.FullTime)]
    public void StudyMode_maps_codes(string? code, string expected) =>
        Assert.Equal(expected, CourseVocabulary.StudyMode(code));

    [Fact]
    public void FundingGroups_collapse_streams_into_two_questions()
    {
        var groups = CourseVocabulary.FundingGroups(["16-18", "16-19", "19+", "Full Cost", "21+ Apprenticeship"]).ToList();

        Assert.Equal([CourseVocabulary.Funding16To18, CourseVocabulary.FundingAdult], groups);
    }

    [Fact]
    public void FundingGroups_ignore_full_cost_only()
    {
        Assert.Empty(CourseVocabulary.FundingGroups(["Full Cost", " "]));
    }

    [Theory]
    [InlineData("Construction", CampusMapping.PrincesRoad)]
    [InlineData("Engineering", CampusMapping.PrincesRoad)]
    [InlineData("Creative Design", CampusMapping.MoulshamStreet)]
    public void Campus_comes_from_the_demo_subject_mapping(string subject, string campus) =>
        Assert.Equal(campus, CampusMapping.ForSubject(subject));
}
