using Chelmsford.Core.Common;

namespace Chelmsford.Tests;

/// <summary>The clocks went back at 02:00 BST (01:00 UTC) on Sunday 25 October 2026.</summary>
public sealed class UkTimeTests
{
    [Fact]
    public void Before_the_change_UTC_is_an_hour_behind_UK_time()
    {
        var local = UkTime.FromUtc(new DateTime(2026, 10, 24, 12, 15, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 10, 24, 13, 15, 0), local);
        Assert.Equal("24 October 2026 at 1:15pm", UkTime.FormatDateTime(local));
    }

    [Fact]
    public void After_the_change_UTC_and_UK_time_agree()
    {
        var local = UkTime.FromUtc(new DateTime(2026, 10, 25, 13, 15, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 10, 25, 13, 15, 0), local);
        Assert.Equal("25 October 2026 at 1:15pm", UkTime.FormatUtc(new DateTime(2026, 10, 25, 13, 15, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void The_early_hours_of_the_change_day_are_read_correctly()
    {
        // 00:30 UTC is 01:30 BST; an hour later 01:30 UTC is 01:30 GMT, the same wall-clock time again.
        Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0), UkTime.FromUtc(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0), UkTime.FromUtc(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 25, 2, 30, 0), UkTime.FromUtc(new DateTime(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void UK_local_time_converts_back_to_UTC_on_either_side_of_the_change()
    {
        Assert.Equal(new DateTime(2026, 10, 24, 12, 15, 0), UkTime.ToUtc(new DateTime(2026, 10, 24, 13, 15, 0)));
        Assert.Equal(new DateTime(2026, 10, 25, 13, 15, 0), UkTime.ToUtc(new DateTime(2026, 10, 25, 13, 15, 0)));
    }

    [Fact]
    public void An_ambiguous_local_time_is_read_as_GMT_and_a_missing_one_moves_on_an_hour()
    {
        // 01:30 happens twice on 25 October 2026; the later (GMT) one is used.
        Assert.Equal(new DateTime(2026, 10, 25, 1, 30, 0), UkTime.ToUtc(new DateTime(2026, 10, 25, 1, 30, 0)));

        // 01:30 never happens on 28 March 2027 (01:00 GMT jumps to 02:00 BST).
        Assert.Equal(new DateTime(2027, 3, 28, 1, 30, 0), UkTime.ToUtc(new DateTime(2027, 3, 28, 1, 30, 0)));
    }

    [Theory]
    [InlineData(13, 15, "1:15pm")]
    [InlineData(17, 0, "5pm")]
    [InlineData(12, 0, "12 noon")]
    [InlineData(0, 5, "12:05am")]
    [InlineData(9, 30, "9:30am")]
    public void Times_read_the_way_people_say_them(int hour, int minute, string expected) =>
        Assert.Equal(expected, UkTime.FormatTime(new DateTime(2026, 10, 25, hour, minute, 0)));

    [Fact]
    public void Dates_and_ranges_are_formatted_in_British_English()
    {
        var start = new DateTime(2026, 10, 25, 17, 0, 0);

        Assert.Equal("25 October 2026", UkTime.FormatDate(start));
        Assert.Equal("Sunday 25 October 2026", UkTime.FormatLongDate(start));
        Assert.Equal("5pm to 8:30pm", UkTime.FormatTimeRange(start, start.AddMinutes(210)));
        Assert.Equal("5pm", UkTime.FormatTimeRange(start, null));
    }

    [Fact]
    public void Stored_dates_without_a_zone_are_taken_as_UK_time_and_UTC_ones_are_converted()
    {
        // Midnight on 11 July 2027 in the UK is 23:00 UTC the evening before.
        Assert.Equal(new DateTime(2027, 7, 11), UkTime.AsLocal(new DateTime(2027, 7, 10, 23, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2027, 7, 11), UkTime.AsLocal(new DateTime(2027, 7, 11, 0, 0, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void NowAt_follows_the_supplied_clock()
    {
        var clock = new Fakes.TestClock(new DateTimeOffset(2026, 10, 24, 23, 30, 0, TimeSpan.Zero));

        // 23:30 UTC on the 24th is 00:30 BST on the 25th.
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0), UkTime.NowAt(clock));
    }
}
