using Chelmsford.Core.Events;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chelmsford.Infrastructure.Events;

/// <summary>Event bookings. Capacity is checked and the booking written in one transaction.</summary>
public sealed class EventBookingService(
    SiteDbContext db,
    TimeProvider clock,
    ILogger<EventBookingService> logger) : IEventBookingService
{
    public async Task<BookingOutcome> BookAsync(BookingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.People, 1);

        if (!request.BookingOpen)
        {
            return BookingOutcome.Closed;
        }

        // SQLite serialises writers, so the capacity check and insert can't interleave with another booking.
        // On SQL Server this transaction would use serializable isolation (or an UPDLOCK on the event row).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (request.Capacity is { } capacity)
        {
            var taken = await PlacesTakenAsync(request.EventKey, cancellationToken);
            if (taken + request.People > capacity)
            {
                logger.LogInformation("Booking for event {EventKey} refused: {Taken}/{Capacity} taken, {People} requested", request.EventKey, taken, capacity, request.People);
                return BookingOutcome.Full;
            }
        }

        var booking = new EventBooking
        {
            Id = Guid.NewGuid(),
            EventKey = request.EventKey,
            EventName = request.EventName.Trim(),
            Name = request.Name.Trim(),
            Email = request.Email.Trim(),
            AttendeeType = request.AttendeeType.Trim(),
            People = request.People,
            CreatedUtc = clock.GetUtcNow().UtcDateTime
        };
        db.EventBookings.Add(booking);

        // Same save as the booking (outbox pattern): a booking is never confirmed without being saved, nor saved without its confirmation.
        db.OutboxEmails.Add(BookingConfirmationEmail.Compose(booking, request, booking.CreatedUtc));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Booked {People} place(s) for event {EventKey}", request.People, request.EventKey);
        return BookingOutcome.Booked;
    }

    public async Task<int> PlacesTakenAsync(Guid eventKey, CancellationToken cancellationToken = default) =>
        await db.EventBookings.Where(b => b.EventKey == eventKey).SumAsync(b => (int?)b.People, cancellationToken) ?? 0;
}
