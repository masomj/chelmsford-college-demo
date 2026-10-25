namespace Chelmsford.Core.Events;

public sealed class EventBooking
{
    public Guid Id { get; set; }
    public Guid EventKey { get; set; }
    public string EventName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string AttendeeType { get; set; } = string.Empty;
    public int People { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public enum BookingOutcome
{
    Booked,
    Full,
    Closed
}

/// <summary>
/// A request to book places. <paramref name="StartsAtLocal"/> and <paramref name="EndsAtLocal"/> are UK wall-clock
/// times as editors enter them in the CMS; with <paramref name="Venue"/> they appear in the confirmation email.
/// </summary>
public sealed record BookingRequest(
    Guid EventKey,
    string EventName,
    int? Capacity,
    bool BookingOpen,
    string Name,
    string Email,
    string AttendeeType,
    int People,
    DateTime? StartsAtLocal = null,
    DateTime? EndsAtLocal = null,
    string? Venue = null);

public interface IEventBookingService
{
    Task<BookingOutcome> BookAsync(BookingRequest request, CancellationToken cancellationToken = default);
    Task<int> PlacesTakenAsync(Guid eventKey, CancellationToken cancellationToken = default);
}

/// <summary>The fixed list of event types editors choose from in the CMS.</summary>
public static class EventTypes
{
    public const string OpenEvents = "Open events";
    public const string TasterDays = "Taster days";
    public const string Advice = "Advice and guidance";
    public const string Employers = "Employers";
    public const string Showcases = "Showcases";

    public static readonly IReadOnlyList<string> All = [OpenEvents, TasterDays, Advice, Employers, Showcases];
}
