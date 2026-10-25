using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Chelmsford.Core.Access;

/// <summary>The kinds of event the access log records.</summary>
public static class AccessKinds
{
    public const string PageView = "page-view";
    public const string CmsLogin = "cms-login";
    public const string CmsLoginFailed = "cms-login-failed";
    public const string CmsLogout = "cms-logout";

    public static readonly IReadOnlyList<string> All = [PageView, CmsLogin, CmsLoginFailed, CmsLogout];
}

/// <summary>
/// One recorded visit or sign-in, kept so the college panel's use of the demo can be reviewed. No full IP address is
/// stored: <see cref="IpPrefix"/> is shortened by <see cref="IpAnonymiser"/>.
/// </summary>
public sealed class AccessLogEntry
{
    public const int MaxUserAgentLength = 200;

    public Guid Id { get; set; }
    public DateTime OccurredUtc { get; set; }

    /// <summary>One of <see cref="AccessKinds"/>.</summary>
    public string Kind { get; set; } = AccessKinds.PageView;

    public string? UserName { get; set; }
    public string? Path { get; set; }

    /// <summary>IPv4 /24 such as "81.2.69.x", or the first three groups of an IPv6 address.</summary>
    public string? IpPrefix { get; set; }

    /// <summary>Truncated to <see cref="MaxUserAgentLength"/> characters.</summary>
    public string? UserAgent { get; set; }
}

/// <summary>A user's activity on one UK calendar day.</summary>
public sealed record AccessDaySummary(DateOnly Day, string UserName, int PageViews, DateTime FirstSeenUtc, DateTime LastSeenUtc);

public interface IAccessLog
{
    /// <summary>
    /// Records an entry. Also deletes entries past the retention period (at most hourly) and queues a notification
    /// email for a user's first page view of the UK day and for every CMS login, when a notification address is set.
    /// </summary>
    Task RecordAsync(AccessLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Newest first, optionally only one <see cref="AccessKinds"/> value.</summary>
    Task<IReadOnlyList<AccessLogEntry>> RecentAsync(int take, string? kind = null, CancellationToken cancellationToken = default);

    /// <summary>Page views per user per UK day over the last <paramref name="days"/> days (today included), newest day first.</summary>
    Task<IReadOnlyList<AccessDaySummary>> DailySummaryAsync(int days, CancellationToken cancellationToken = default);
}

/// <summary>Shortens IP addresses so the log shows roughly where a visit came from without identifying a device.</summary>
public static class IpAnonymiser
{
    /// <summary>"81.2.69.142" becomes "81.2.69.x"; "2001:db8:85a3:8d3::1" becomes "2001:db8:85a3:x". Returns null for anything that isn't an IP address.</summary>
    public static string? Prefix(string? ipAddress) =>
        IPAddress.TryParse(ipAddress?.Trim(), out var parsed) ? Prefix(parsed) : null;

    public static string? Prefix(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        switch (address.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                return string.Create(CultureInfo.InvariantCulture, $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x");
            case AddressFamily.InterNetworkV6:
                var groups = Enumerable.Range(0, 3)
                    .Select(i => ((bytes[i * 2] << 8) | bytes[(i * 2) + 1]).ToString("x", CultureInfo.InvariantCulture));
                return string.Join(':', groups) + ":x";
            default:
                return null;
        }
    }
}
