using System.Globalization;
using CoreUkTime = Chelmsford.Core.Common.UkTime;

namespace Chelmsford.Web.Services;

/// <summary>
/// Presentation helpers for event, campaign and report times, shown in UK time (Europe/London). The time zone and the
/// core conversions come from <see cref="CoreUkTime"/> so the whole site agrees on them.
/// </summary>
public static class UkTime
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-GB");

    public static TimeZoneInfo London => CoreUkTime.London;

    public static DateTime NowLocal => CoreUkTime.Now;

    public static DateTime ToUtc(DateTime local) => CoreUkTime.ToUtc(local);

    public static DateTime FromUtc(DateTime utc) => CoreUkTime.FromUtc(utc);

    /// <summary>"Wednesday 18 November 2026"</summary>
    public static string LongDate(DateTime value) => CoreUkTime.FormatLongDate(value);

    /// <summary>"Wednesday 18 November"</summary>
    public static string DayAndDate(DateTime value) => value.ToString("dddd d MMMM", Culture);

    /// <summary>"18 November 2026"</summary>
    public static string Date(DateTime value) => CoreUkTime.FormatDate(value);

    /// <summary>"5pm", "9:30am", "12 noon"</summary>
    public static string Time(DateTime value) => CoreUkTime.FormatTime(value);

    /// <summary>"5pm to 8pm"</summary>
    public static string TimeRange(DateTime start, DateTime? end) => CoreUkTime.FormatTimeRange(start, end);

    /// <summary>"18 Nov 2026, 1:15pm" for compact report tables.</summary>
    public static string ShortDateTime(DateTime utc)
    {
        var local = CoreUkTime.FromUtc(utc);
        return local.ToString("d MMM yyyy", Culture) + ", " + CoreUkTime.FormatTime(local);
    }

    public static string MonthAbbrev(DateTime value) => value.ToString("MMM", Culture).ToUpperInvariant();

    public static string MonthKey(DateTime value) => value.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public static string MonthLabel(DateTime value) => value.ToString("MMMM yyyy", Culture);

    public static string Money(int pence) => (pence / 100m).ToString("£#,##0.00", CultureInfo.InvariantCulture);
}
