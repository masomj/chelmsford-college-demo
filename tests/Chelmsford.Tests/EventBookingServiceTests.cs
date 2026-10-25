using Chelmsford.Core.Events;
using Chelmsford.Infrastructure.Events;
using Chelmsford.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chelmsford.Tests;

public sealed class EventBookingServiceTests : IDisposable
{
    private static readonly Guid OpenEvening = Guid.Parse("5b9f2c1e-0000-4000-8000-0000000000e1");

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private async Task<BookingOutcome> BookAsync(int people, int? capacity = 5, bool open = true)
    {
        await using var db = _database.CreateContext();
        var service = new EventBookingService(db, new TestClock(), NullLogger<EventBookingService>.Instance);
        return await service.BookAsync(new BookingRequest(OpenEvening, "Open evening", capacity, open, "Sam", "sam@example.com", "Parent or carer", people));
    }

    [Fact]
    public async Task Bookings_fill_up_to_capacity()
    {
        Assert.Equal(BookingOutcome.Booked, await BookAsync(3));
        Assert.Equal(BookingOutcome.Full, await BookAsync(3));
        Assert.Equal(BookingOutcome.Booked, await BookAsync(2));
        Assert.Equal(BookingOutcome.Full, await BookAsync(1));

        await using var db = _database.CreateContext();
        Assert.Equal(5, await new EventBookingService(db, new TestClock(), NullLogger<EventBookingService>.Instance).PlacesTakenAsync(OpenEvening));
    }

    [Fact]
    public async Task Closed_events_refuse_bookings_and_unlimited_events_accept_them()
    {
        Assert.Equal(BookingOutcome.Closed, await BookAsync(1, open: false));
        Assert.Equal(BookingOutcome.Booked, await BookAsync(50, capacity: null));
    }

    [Fact]
    public async Task A_booking_queues_a_confirmation_email_in_the_same_save()
    {
        await using (var db = _database.CreateContext())
        {
            var service = new EventBookingService(db, new TestClock(), NullLogger<EventBookingService>.Instance);
            var outcome = await service.BookAsync(new BookingRequest(
                OpenEvening, "Open evening", 10, true, " Sam Jones ", "sam@example.com", "Parent or carer", 3,
                StartsAtLocal: new DateTime(2026, 10, 28, 17, 30, 0),
                EndsAtLocal: new DateTime(2026, 10, 28, 20, 0, 0),
                Venue: "Main campus, Block A"));
            Assert.Equal(BookingOutcome.Booked, outcome);
        }

        await using var check = _database.CreateContext();
        var booking = await check.EventBookings.SingleAsync();
        var email = await check.OutboxEmails.SingleAsync();
        Assert.Equal($"booking:{booking.Id:D}", email.CorrelationKey);
        Assert.Equal("sam@example.com", email.To);
        Assert.Equal("Booking confirmed: Open evening", email.Subject);
        Assert.Contains("Dear Sam Jones,", email.Body, StringComparison.Ordinal);
        Assert.Contains("Event: Open evening", email.Body, StringComparison.Ordinal);
        Assert.Contains("When: 28 October 2026 at 5:30pm to 8pm (UK time)", email.Body, StringComparison.Ordinal);
        Assert.Contains("Where: Main campus, Block A", email.Body, StringComparison.Ordinal);
        Assert.Contains("Number of people: 3", email.Body, StringComparison.Ordinal);
        Assert.Null(email.SentUtc);
    }

    [Fact]
    public async Task The_confirmation_leaves_out_what_it_was_not_told()
    {
        Assert.Equal(BookingOutcome.Booked, await BookAsync(1));

        await using var check = _database.CreateContext();
        var email = await check.OutboxEmails.SingleAsync();
        Assert.DoesNotContain("When:", email.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Where:", email.Body, StringComparison.Ordinal);
        Assert.Contains("Number of people: 1", email.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_bookings_queue_no_email()
    {
        Assert.Equal(BookingOutcome.Closed, await BookAsync(1, open: false));
        Assert.Equal(BookingOutcome.Full, await BookAsync(9, capacity: 5));

        await using var check = _database.CreateContext();
        Assert.Empty(await check.OutboxEmails.ToListAsync());
    }

    [Fact]
    public async Task Each_booking_gets_its_own_confirmation()
    {
        await BookAsync(1);
        await BookAsync(1);

        await using var check = _database.CreateContext();
        Assert.Equal(2, (await check.OutboxEmails.Select(e => e.CorrelationKey).Distinct().ToListAsync()).Count);
    }
}
