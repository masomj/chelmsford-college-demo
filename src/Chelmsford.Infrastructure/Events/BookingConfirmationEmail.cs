using System.Text;
using Chelmsford.Core.Common;
using Chelmsford.Core.Events;
using Chelmsford.Core.Messaging;

namespace Chelmsford.Infrastructure.Events;

/// <summary>Writes the confirmation email queued in the outbox when an event booking is saved.</summary>
internal static class BookingConfirmationEmail
{
    public static string CorrelationKey(Guid bookingId) => $"booking:{bookingId:D}";

    public static OutboxEmail Compose(EventBooking booking, BookingRequest request, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(request);

        var body = new StringBuilder()
            .Append("Dear ").Append(booking.Name).AppendLine(",")
            .AppendLine()
            .Append("Thank you. Your booking for ").Append(booking.EventName).AppendLine(" at Chelmsford College is confirmed.")
            .AppendLine()
            .Append("Event: ").AppendLine(booking.EventName);

        if (request.StartsAtLocal is { } starts)
        {
            body.Append("When: ").Append(UkTime.FormatDateTime(starts));
            if (request.EndsAtLocal is { } ends && ends > starts)
            {
                // Same day: "... at 5pm to 8pm"; a later day: spell the end out in full.
                body.Append(ends.Date == starts.Date ? " to " + UkTime.FormatTime(ends) : " to " + UkTime.FormatDateTime(ends));
            }

            body.AppendLine(" (UK time)");
        }

        if (!string.IsNullOrWhiteSpace(request.Venue))
        {
            body.Append("Where: ").AppendLine(request.Venue.Trim());
        }

        body.Append("Number of people: ").AppendLine(booking.People.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("If your plans change, please reply to this email and let us know so we can offer your place to someone else.")
            .AppendLine()
            .AppendLine("Chelmsford College");

        return new OutboxEmail
        {
            Id = Guid.NewGuid(),
            CorrelationKey = CorrelationKey(booking.Id),
            To = booking.Email,
            Subject = $"Booking confirmed: {booking.EventName}",
            Body = body.ToString(),
            CreatedUtc = createdUtc
        };
    }
}
