using Chelmsford.Infrastructure.Integration;

namespace Chelmsford.Tests;

public sealed class HtmlCleanerTests
{
    private readonly HtmlCleaner _cleaner = new();

    [Theory]
    [InlineData(null)]
    [InlineData("NULL")]
    [InlineData("_x000D_\n")]
    [InlineData("<p>&nbsp;</p>_x000D_\n<p> </p>")]
    public void Missing_values_become_null(string? raw) =>
        Assert.Null(_cleaner.Clean(raw));

    [Fact]
    public void Glyphicon_buttons_become_plain_links()
    {
        const string raw =
            "<p><a class=\"btn btn-info btn-lg btn-block\" href=\"https://www.gov.uk/become-apprentice\" title=\"More\">" +
            "<span class=\"glyphicon glyphicon-new-window\" aria-hidden=\"true\"></span> More information</a></p>";

        Assert.Equal("<p><a href=\"https://www.gov.uk/become-apprentice\">More information</a></p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void In_page_anchor_buttons_are_unwrapped_to_text()
    {
        const string raw = "<p><a class=\"btn btn-success\" href=\"#courselist\"><span class=\"glyphicon glyphicon-book\"></span> View courses</a></p>";

        Assert.Equal("<p>View courses</p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Styles_classes_and_ids_are_stripped()
    {
        const string raw = "<p style=\"text-align: justify\" class=\"lead\" id=\"x\">Hands-on <em class=\"y\">learning</em></p>";

        Assert.Equal("<p>Hands-on <em>learning</em></p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Bold_only_paragraph_becomes_a_heading()
    {
        const string raw = "<p><strong>What you will learn&nbsp;</strong></p>_x000D_\n<p>Pattern cutting.</p>";

        Assert.Equal("<h3>What you will learn</h3><p>Pattern cutting.</p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Bold_inside_a_sentence_is_kept_as_bold()
    {
        const string raw = "<p>4 GCSEs grades 9-4&nbsp;<strong>OR </strong>a relevant Level 2</p>";

        Assert.Equal("<p>4 GCSEs grades 9-4 <strong>OR </strong>a relevant Level 2</p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Embeds_scripts_and_wrappers_are_removed_keeping_text()
    {
        const string raw =
            "<div class=\"row\"><div class=\"col\"><p>Visit our open day.</p></div></div>" +
            "<iframe src=\"https://www.youtube.com/embed/x\"></iframe><script>alert(1)</script><style>p{}</style>" +
            "<div>Loose text</div>";

        Assert.Equal("<p>Visit our open day.</p><p>Loose text</p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Unsafe_link_schemes_are_dropped()
    {
        const string raw = "<p><a href=\"javascript:alert(1)\">Click</a> or <a href=\"mailto:info@example.ac.uk\">email</a></p>";

        Assert.Equal("<p>Click or <a href=\"mailto:info@example.ac.uk\">email</a></p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Empty_paragraphs_and_trailing_nbsp_are_removed()
    {
        const string raw = "<p>Internally assessed course work and portfolio&nbsp;</p>_x000D_\n<p>&nbsp;</p><p></p>";

        Assert.Equal("<p>Internally assessed course work and portfolio</p>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Lists_are_kept()
    {
        const string raw = "<ul><li>Health Professions </li><li>Nursing</li><li>&nbsp;</li></ul>";

        Assert.Equal("<ul><li>Health Professions</li><li>Nursing</li></ul>", _cleaner.Clean(raw));
    }

    [Fact]
    public void Cleaning_is_deterministic()
    {
        const string raw = "<p style=\"x\"><b>Fees</b></p><p>&pound;150&nbsp;</p>_x000D_\n";

        Assert.Equal(_cleaner.Clean(raw), _cleaner.Clean(raw));
        Assert.Equal("<h3>Fees</h3><p>£150</p>", _cleaner.Clean(raw));
    }
}
