using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Chelmsford.Core.Events;
using Chelmsford.Web.Models;
using Chelmsford.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Chelmsford.Web.Controllers;

/// <summary>Event booking (POST, redirect, GET) and the add-to-calendar file.</summary>
public sealed class BookingController(SiteContent content, IEventBookingService bookings, ILogger<BookingController> logger) : Controller
{
    [HttpPost("/events/book")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Book([FromForm] BookingForm form, CancellationToken cancellationToken)
    {
        if (content.EventByKey(form.EventKey) is not { } evt)
        {
            return SiteResults.NotFoundView(this);
        }

        var errors = Validate(form);
        if (errors.Count > 0)
        {
            TempData["BookingErrors"] = JsonSerializer.Serialize(errors);
            TempData["BookingForm"] = JsonSerializer.Serialize(form);
            return Redirect(evt.Url + "#book");
        }

        var outcome = await bookings.BookAsync(
            new BookingRequest(evt.Key, evt.Name, evt.Capacity, evt.BookingOpen && (evt.EndsAt ?? evt.StartsAt) > UkTime.NowLocal,
                form.Name!.Trim(), form.Email!.Trim(), form.AttendeeType!, form.People!.Value,
                StartsAtLocal: evt.StartsAt, EndsAtLocal: evt.EndsAt, Venue: evt.Venue),
            cancellationToken);

        // The result is passed to the event page through TempData, so a reload of that page doesn't
        // repeat it and the address stays clean.
        TempData[EventController.BookedKey] = EventController.BookingResult(evt.Key, outcome switch
        {
            BookingOutcome.Booked => "booked",
            BookingOutcome.Full => "full",
            _ => "closed"
        });
        return Redirect(evt.Url + "#book");
    }

    private static Dictionary<string, string> Validate(BookingForm form)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(form.Name))
        {
            errors["Name"] = "Enter your full name";
        }
        else if (form.Name.Trim().Length > 100)
        {
            errors["Name"] = "Full name must be 100 characters or fewer";
        }

        if (string.IsNullOrWhiteSpace(form.Email))
        {
            errors["Email"] = "Enter your email address";
        }
        else if (!IsEmail(form.Email))
        {
            errors["Email"] = "Enter an email address in the correct format, like name@example.com";
        }

        if (string.IsNullOrWhiteSpace(form.AttendeeType) || !EventViewModel.AttendeeTypes.Contains(form.AttendeeType))
        {
            errors["AttendeeType"] = "Select who you are";
        }

        if (form.People is not (>= 1 and <= 4))
        {
            errors["People"] = "Select how many people are coming, from 1 to 4";
        }

        return errors;
    }

    public static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254)
        {
            return false;
        }

        try
        {
            var address = new MailAddress(value.Trim());
            return address.Address == value.Trim() && address.Host.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>An RFC 5545 calendar file for one event, with times in UTC.</summary>
    [HttpGet("/events/calendar/{key:guid}.ics")]
    public IActionResult Calendar(Guid key)
    {
        if (content.EventByKey(key) is not { } evt)
        {
            return SiteResults.NotFoundView(this);
        }

        var origin = $"{Request.Scheme}://{Request.Host}";
        var start = UkTime.ToUtc(evt.StartsAt);
        var end = UkTime.ToUtc(evt.EndsAt is { } e && e > evt.StartsAt ? e : evt.StartsAt.AddHours(1));
        string Stamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var location = string.Join(", ", new[] { evt.Venue, evt.Address?.Replace("\r", string.Empty).Replace("\n", ", ") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Chelmsford College//Website events//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{evt.Key:D}@chelmsford-college-demo",
            $"DTSTAMP:{Stamp(DateTime.UtcNow)}",
            $"DTSTART:{Stamp(start)}",
            $"DTEND:{Stamp(end)}",
            $"SUMMARY:{Escape(evt.Name + ", Chelmsford College")}",
            $"DESCRIPTION:{Escape((evt.Summary ?? string.Empty) + "\nFree. Booking required: " + origin + evt.Url)}",
            $"LOCATION:{Escape(location)}",
            $"URL:{origin}{evt.Url}",
            "END:VEVENT",
            "END:VCALENDAR"
        };

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            Fold(builder, line);
        }

        var fileName = Core.Common.SourceText.Slug(evt.Name) + ".ics";
        logger.LogDebug("Calendar file for {Event}", evt.Key);
        return File(Encoding.UTF8.GetBytes(builder.ToString()), "text/calendar; charset=utf-8", fileName);
    }

    /// <summary>Escapes TEXT values (RFC 5545 section 3.3.11).</summary>
    public static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal);

    /// <summary>Folds content lines longer than 75 octets, ending every line with CRLF.</summary>
    private static void Fold(StringBuilder builder, string line)
    {
        var bytes = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            var limit = first ? 75 : 74;
            if (bytes + size > limit)
            {
                builder.Append("\r\n ");
                bytes = 0;
                first = false;
            }

            builder.Append(rune.ToString());
            bytes += size;
        }

        builder.Append("\r\n");
    }
}
