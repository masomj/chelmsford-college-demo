using System.Globalization;

namespace Chelmsford.Core.Common;

/// <summary>
/// UK civil time (Europe/London, which moves between GMT and BST). Everything stored by the site is UTC;
/// anything shown to a person, or compared against a date a member of staff typed in, is UK time.
/// </summary>
/// <remarks>
/// Local values are <see cref="DateTimeKind.Unspecified"/> wall-clock times. Formatting uses the invariant
/// culture's English names so it doesn't depend on ICU data being present on the host.
/// </remarks>
public static class UkTime
{
    public static readonly TimeZoneInfo London = FindLondon();

    /// <summary>The current UK wall-clock time.</summary>
    public static DateTime Now => FromUtc(DateTime.UtcNow);

    /// <summary>The UK wall-clock time at the instant the given clock reports (lets tests control the date).</summary>
    public static DateTime NowAt(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return FromUtc(clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>Converts a UTC instant to UK wall-clock time. A value with no kind is taken to be UTC.</summary>
    public static DateTime FromUtc(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), London);

    /// <summary>
    /// Converts a UK wall-clock time to UTC. A time that doesn't exist (the hour skipped when the clocks go
    /// forward) moves on an hour; a time that happens twice (when they go back) is read as the later,
    /// GMT, occurrence.
    /// </summary>
    public static DateTime ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (London.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, London);
    }

    /// <summary>
    /// Reads a stored date as UK wall-clock time: a UTC value is converted, anything else is taken to already
    /// be UK time (dates typed into the CMS carry no zone).
    /// </summary>
    public static DateTime AsLocal(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? FromUtc(value) : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    /// <summary>"25 October 2026"</summary>
    public static string FormatDate(DateTime local) => local.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"Sunday 25 October 2026"</summary>
    public static string FormatLongDate(DateTime local) => local.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"5pm", "1:15pm", "12 noon"</summary>
    public static string FormatTime(DateTime local)
    {
        if (local.Hour == 12 && local.Minute == 0)
        {
            return "12 noon";
        }

        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        var suffix = local.Hour < 12 ? "am" : "pm";
        return local.Minute == 0
            ? hour.ToString(CultureInfo.InvariantCulture) + suffix
            : hour.ToString(CultureInfo.InvariantCulture) + ":" + local.Minute.ToString("00", CultureInfo.InvariantCulture) + suffix;
    }

    /// <summary>"25 October 2026 at 1:15pm"</summary>
    public static string FormatDateTime(DateTime local) => $"{FormatDate(local)} at {FormatTime(local)}";

    /// <summary>"5pm to 8pm", or just the start time when there is no later end.</summary>
    public static string FormatTimeRange(DateTime startLocal, DateTime? endLocal) =>
        endLocal is { } end && end > startLocal ? $"{FormatTime(startLocal)} to {FormatTime(end)}" : FormatTime(startLocal);

    /// <summary>Formats a UTC instant as UK date and time: "25 October 2026 at 1:15pm".</summary>
    public static string FormatUtc(DateTime utc) => FormatDateTime(FromUtc(utc));

    private static TimeZoneInfo FindLondon()
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new TimeZoneNotFoundException("The UK time zone (Europe/London or GMT Standard Time) is not available on this host. Install tzdata.");
    }
}
