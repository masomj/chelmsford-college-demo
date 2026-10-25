using Chelmsford.Core.Common;

namespace Chelmsford.Tests;

public sealed class SourceTextTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NULL")]
    [InlineData("null")]
    [InlineData(" NULL ")]
    [InlineData("_x000D_\n")]
    [InlineData("<p>&nbsp;</p>_x000D_\n")]
    [InlineData("&nbsp;")]
    public void Clean_treats_missing_values_as_null(string? raw) =>
        Assert.Null(SourceText.Clean(raw));

    [Fact]
    public void Clean_removes_carriage_return_artefacts_and_trims()
    {
        Assert.Equal("<p>2 years</p>", SourceText.Clean("  <p>2 years</p>_x000D_\r\n"));
    }

    [Fact]
    public void PlainText_strips_tags_and_decodes_entities()
    {
        Assert.Equal("£150 per year", SourceText.PlainText("<p>&pound;150&nbsp;per <strong>year</strong></p>"));
        Assert.Equal("£150 per year", SourceText.PlainText(SourceText.Clean("<p>&pound;150&nbsp;per <strong>year</strong></p>_x000D_")));
    }

    [Fact]
    public void FirstEmail_survives_a_broken_mailto_anchor()
    {
        const string raw = "<p><a href=\"mailto:info@example.ac.uk\">c</a>ourses@example.ac.uk&nbsp;</p>";

        Assert.Equal("courses@example.ac.uk", SourceText.FirstEmail(raw));
    }

    [Theory]
    [InlineData("<p>courses@example.ac.uk</p>_x000D_\n", "courses@example.ac.uk")]
    [InlineData("<p><a href=\"mailto:info@example.ac.uk\">Info@Example.ac.uk</a></p>", "info@example.ac.uk")]
    [InlineData("NULL", null)]
    [InlineData("<p>Call us</p>", null)]
    public void FirstEmail_finds_the_address_in_the_text(string raw, string? expected) =>
        Assert.Equal(expected, SourceText.FirstEmail(raw));

    [Theory]
    [InlineData("Example Level 3 Extended Diploma in Garments & Patterns", "example-level-3-extended-diploma-in-garments-patterns")]
    [InlineData("  Café / Barista: Level 2 ", "cafe-barista-level-2")]
    public void Slug_is_lower_case_and_hyphenated(string value, string expected) =>
        Assert.Equal(expected, SourceText.Slug(value));
}
