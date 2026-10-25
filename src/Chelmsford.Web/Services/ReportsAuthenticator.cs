using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Chelmsford.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Chelmsford.Web.Services;

public enum ReportsAuthResult
{
    Allowed,
    Disabled,
    Challenge,
    TooManyAttempts
}

/// <summary>
/// HTTP Basic sign-in for the access reports, done in the app so the reports are protected even if the reverse proxy
/// is bypassed. Wrong attempts are counted per client; after five in fifteen minutes the client is refused until
/// the window passes.
/// </summary>
public sealed class ReportsAuthenticator(IOptions<ReportsOptions> options, TimeProvider clock)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Started)> _failures = new();

    public ReportsAuthResult Check(HttpRequest request)
    {
        var settings = options.Value;
        if (!settings.IsConfigured)
        {
            return ReportsAuthResult.Disabled;
        }

        var client = request.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = clock.GetUtcNow();
        if (_failures.TryGetValue(client, out var seen))
        {
            if (now - seen.Started > Window)
            {
                _failures.TryRemove(client, out _);
            }
            else if (seen.Count >= MaxFailures)
            {
                return ReportsAuthResult.TooManyAttempts;
            }
        }

        if (!TryReadCredentials(request, out var user, out var password))
        {
            // No credentials yet is the browser's first request, not a failed guess.
            return ReportsAuthResult.Challenge;
        }

        if (Same(user, settings.UserName) & Same(password, settings.Password!))
        {
            _failures.TryRemove(client, out _);
            return ReportsAuthResult.Allowed;
        }

        _failures.AddOrUpdate(client, _ => (1, now), (_, old) => now - old.Started > Window ? (1, now) : (old.Count + 1, old.Started));
        return ReportsAuthResult.Challenge;
    }

    private static bool TryReadCredentials(HttpRequest request, out string user, out string password)
    {
        user = password = string.Empty;
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return false;
            }

            user = decoded[..colon];
            password = decoded[(colon + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Compares without stopping at the first difference, by hashing both sides to equal length first.</summary>
    private static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
