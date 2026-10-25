using Chelmsford.Infrastructure.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>
/// Limits guest lookups so student IDs and dates of birth can't be guessed: per client, and per student ID
/// across all clients (so an attacker can't dodge the first limit by changing address).
/// </summary>
/// <remarks>
/// Checking and counting are one step: <see cref="TryBegin"/> takes an attempt before the lookup is made, and
/// <see cref="Release"/> gives it back if the lookup turns out to be right. Concurrent requests therefore can't
/// all slip under the limit between a check and a later "record failure".
/// </remarks>
public interface IGuestLookupLimiter
{
    /// <summary>Failed attempts the client has left in the current window.</summary>
    int AttemptsLeft(string clientKey);

    /// <summary>
    /// Atomically checks both limits and, if neither is used up, counts an attempt against each. When the result is
    /// not allowed nothing is counted. <paramref name="studentId"/> may be empty, in which case only the client limit applies.
    /// </summary>
    GuestLookupAttempt TryBegin(string clientKey, string? studentId);

    /// <summary>Gives back an attempt that turned out to be a successful lookup, so only failures count.</summary>
    void Release(GuestLookupAttempt attempt);
}

/// <summary>
/// One counted lookup. <see cref="AttemptsLeft"/> is the smaller of what the client and the student ID have left
/// after this attempt, so if the lookup fails it is the number to tell the person.
/// </summary>
public sealed class GuestLookupAttempt
{
    internal GuestLookupAttempt(bool allowed, int attemptsLeft, object? clientWindow = null, object? studentWindow = null)
    {
        Allowed = allowed;
        AttemptsLeft = attemptsLeft;
        ClientWindow = clientWindow;
        StudentWindow = studentWindow;
    }

    public bool Allowed { get; }

    public int AttemptsLeft { get; }

    internal object? ClientWindow { get; }

    internal object? StudentWindow { get; }
}

/// <summary>
/// Fixed-window counters held in memory: a window starts at the first failure and lasts
/// <see cref="PaymentOptions.GuestLookupWindowMinutes"/>. Successful lookups do not reset it.
/// </summary>
public sealed class GuestLookupLimiter(IMemoryCache cache, IOptions<PaymentOptions> options, TimeProvider clock) : IGuestLookupLimiter
{
    // One lock covers every counter. Each operation is a handful of dictionary reads, so contention is negligible
    // and a single lock makes check-and-count atomic for every key at once.
    private readonly Lock _lock = new();

    private int MaxPerClient => Math.Max(1, options.Value.GuestLookupMaxFailures);

    private int MaxPerStudent => Math.Max(1, options.Value.GuestLookupMaxFailuresPerStudent);

    public int AttemptsLeft(string clientKey)
    {
        lock (_lock)
        {
            return Left(Current(ClientCacheKey(clientKey)), MaxPerClient);
        }
    }

    public GuestLookupAttempt TryBegin(string clientKey, string? studentId)
    {
        lock (_lock)
        {
            var clientKeyName = ClientCacheKey(clientKey);
            var studentKeyName = StudentCacheKey(studentId);

            var clientWindow = Current(clientKeyName);
            var studentWindow = studentKeyName is null ? null : Current(studentKeyName);

            if (Left(clientWindow, MaxPerClient) <= 0 || (studentKeyName is not null && Left(studentWindow, MaxPerStudent) <= 0))
            {
                return new GuestLookupAttempt(false, 0);
            }

            clientWindow ??= Start(clientKeyName);
            clientWindow.Failures++;

            if (studentKeyName is not null)
            {
                studentWindow ??= Start(studentKeyName);
                studentWindow.Failures++;
            }

            var left = Math.Min(Left(clientWindow, MaxPerClient), studentWindow is null ? int.MaxValue : Left(studentWindow, MaxPerStudent));
            return new GuestLookupAttempt(true, left, clientWindow, studentWindow);
        }
    }

    public void Release(GuestLookupAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        lock (_lock)
        {
            // Only give the attempt back to the window it was taken from; a window that has since ended is gone.
            foreach (var window in new[] { attempt.ClientWindow as Window, attempt.StudentWindow as Window })
            {
                if (window is { Failures: > 0 } && window.ExpiresUtc > clock.GetUtcNow())
                {
                    window.Failures--;
                }
            }
        }
    }

    private static int Left(Window? window, int max) => Math.Max(0, max - (window?.Failures ?? 0));

    private Window? Current(string key) =>
        cache.TryGetValue(key, out Window? window) && window is not null && window.ExpiresUtc > clock.GetUtcNow()
            ? window
            : null;

    private Window Start(string key)
    {
        var length = TimeSpan.FromMinutes(Math.Max(1, options.Value.GuestLookupWindowMinutes));
        var window = new Window(clock.GetUtcNow().Add(length));

        // The cache evicts on its own clock (relative expiry); the window's end is checked on ours.
        cache.Set(key, window, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = length, Size = 1 });
        return window;
    }

    private static string ClientCacheKey(string clientKey) => "guest-lookup:" + (clientKey ?? string.Empty).Trim();

    private static string? StudentCacheKey(string? studentId) =>
        string.IsNullOrWhiteSpace(studentId) ? null : "guest-lookup-student:" + studentId.Trim().ToUpperInvariant();

    private sealed class Window(DateTimeOffset expiresUtc)
    {
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public int Failures { get; set; }
    }
}
