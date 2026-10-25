using System.Globalization;
using System.Text;
using Chelmsford.Core.Access;
using Chelmsford.Core.Common;
using Chelmsford.Core.Messaging;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Access;

/// <summary>
/// Access log over the site database. Recording an entry can also queue a notification email in the same save
/// (outbox pattern), and clears out entries past the retention period.
/// </summary>
public sealed class AccessLogService(
    SiteDbContext db,
    AccessLogRetentionGate retention,
    IOptions<AccessOptions> options,
    TimeProvider clock,
    ILogger<AccessLogService> logger) : IAccessLog
{
    private const int MaxUserNameLength = 200;
    private const int MaxPathLength = 500;
    private const int MaxIpPrefixLength = 64;

    public async Task RecordAsync(AccessLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!AccessKinds.All.Contains(entry.Kind))
        {
            throw new ArgumentException($"Unknown access kind '{entry.Kind}'.", nameof(entry));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var record = new AccessLogEntry
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            OccurredUtc = entry.OccurredUtc == default ? now : DateTime.SpecifyKind(entry.OccurredUtc, DateTimeKind.Utc),
            Kind = entry.Kind,
            UserName = Clean(entry.UserName, MaxUserNameLength),
            Path = Clean(entry.Path, MaxPathLength),
            IpPrefix = Clean(entry.IpPrefix, MaxIpPrefixLength),
            UserAgent = Clean(entry.UserAgent, AccessLogEntry.MaxUserAgentLength)
        };

        var notifyTo = options.Value.NotifyEmail?.Trim();
        var notification = string.IsNullOrEmpty(notifyTo)
            ? null
            : await NotificationAsync(record, notifyTo, now, cancellationToken);

        db.AccessLog.Add(record);
        if (notification is not null)
        {
            db.OutboxEmails.Add(notification);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (notification is not null && DbExceptions.IsUniqueViolation(ex))
        {
            // A parallel request queued the same notification first (the correlation key is unique), so it is
            // already on its way: keep the log entry and drop the duplicate email.
            db.ChangeTracker.Clear();
            db.AccessLog.Add(record);
            await db.SaveChangesAsync(cancellationToken);
        }

        await PruneAsync(now, cancellationToken);
    }

    public async Task<IReadOnlyList<AccessLogEntry>> RecentAsync(int take, string? kind = null, CancellationToken cancellationToken = default)
    {
        var query = db.AccessLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(e => e.Kind == kind);
        }

        return await query
            .OrderByDescending(e => e.OccurredUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AccessDaySummary>> DailySummaryAsync(int days, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, 366);
        var firstDay = UkTime.NowAt(clock).Date.AddDays(-(days - 1));
        var startUtc = UkTime.ToUtc(firstDay);

        var views = await db.AccessLog.AsNoTracking()
            .Where(e => e.Kind == AccessKinds.PageView && e.OccurredUtc >= startUtc)
            .Select(e => new { e.OccurredUtc, e.UserName })
            .ToListAsync(cancellationToken);

        return views
            .GroupBy(v => (Day: DateOnly.FromDateTime(UkTime.FromUtc(v.OccurredUtc)), User: string.IsNullOrWhiteSpace(v.UserName) ? "(unknown)" : v.UserName))
            .Select(g => new AccessDaySummary(g.Key.Day, g.Key.User, g.Count(), g.Min(v => v.OccurredUtc), g.Max(v => v.OccurredUtc)))
            .OrderByDescending(s => s.Day)
            .ThenBy(s => s.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A notification for a CMS login, or for a user's first page view of the UK day; otherwise null.</summary>
    private async Task<OutboxEmail?> NotificationAsync(AccessLogEntry entry, string to, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (entry.Kind == AccessKinds.CmsLogin)
        {
            return AccessNotificationEmail.Compose(entry, to, $"login:{entry.Id:D}", "signed in to the Chelmsford demo CMS", nowUtc);
        }

        if (entry.Kind != AccessKinds.PageView || string.IsNullOrWhiteSpace(entry.UserName))
        {
            return null;
        }

        var ukDay = UkTime.FromUtc(entry.OccurredUtc).Date;
        var dayStartUtc = UkTime.ToUtc(ukDay);
        var dayEndUtc = UkTime.ToUtc(ukDay.AddDays(1));
        var user = entry.UserName;
        var seenToday = await db.AccessLog.AsNoTracking().AnyAsync(
            e => e.Kind == AccessKinds.PageView && e.UserName == user && e.OccurredUtc >= dayStartUtc && e.OccurredUtc < dayEndUtc,
            cancellationToken);

        return seenToday
            ? null
            : AccessNotificationEmail.Compose(entry, to, $"access:{user}:{ukDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", "viewed the Chelmsford demo", nowUtc);
    }

    private async Task PruneAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!retention.TryEnter())
        {
            return;
        }

        try
        {
            var cutoff = nowUtc.AddDays(-Math.Max(1, options.Value.RetentionDays));
            var deleted = await db.AccessLog.Where(e => e.OccurredUtc < cutoff).ExecuteDeleteAsync(cancellationToken);
            if (deleted > 0)
            {
                logger.LogInformation("Deleted {Count} access log entries older than {Cutoff:u}", deleted, cutoff);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Housekeeping must never fail the request that triggered it.
            logger.LogWarning(ex, "Could not clear old access log entries");
        }
    }

    private static string? Clean(string? value, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}

/// <summary>Lets the retention clean-up run at most once an hour, however many entries are recorded. Register as a singleton.</summary>
public sealed class AccessLogRetentionGate(TimeProvider clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private DateTimeOffset _lastRun = DateTimeOffset.MinValue;

    public bool TryEnter()
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow();
            if (now - _lastRun < Interval)
            {
                return false;
            }

            _lastRun = now;
            return true;
        }
    }
}

internal static class AccessNotificationEmail
{
    public static OutboxEmail Compose(AccessLogEntry entry, string to, string correlationKey, string action, DateTime createdUtc)
    {
        var user = string.IsNullOrWhiteSpace(entry.UserName) ? "Someone" : entry.UserName;
        var body = new StringBuilder()
            .Append(user).Append(' ').Append(action).AppendLine(".")
            .AppendLine()
            .Append("When: ").AppendLine(UkTime.FormatDateTime(UkTime.FromUtc(entry.OccurredUtc)) + " (UK time)")
            .Append("User: ").AppendLine(user)
            .Append("IP (shortened): ").AppendLine(entry.IpPrefix ?? "(unknown)")
            .Append("Page: ").AppendLine(entry.Path ?? "(unknown)")
            .ToString();

        return new OutboxEmail
        {
            Id = Guid.NewGuid(),
            CorrelationKey = correlationKey,
            To = to,
            Subject = $"{user} {action}",
            Body = body,
            CreatedUtc = createdUtc
        };
    }
}
