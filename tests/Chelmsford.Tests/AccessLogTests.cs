using System.Net;
using Chelmsford.Core.Access;
using Chelmsford.Infrastructure.Access;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chelmsford.Tests;

public sealed class AccessLogTests : IDisposable
{
    private readonly TestDatabase _database = new();

    // 09:00 UTC on 1 October 2026, which is 10:00 BST.
    private readonly TestClock _clock = new();
    private readonly AccessLogRetentionGate _gate;
    private readonly AccessOptions _options = new() { NotifyEmail = "mason@example.com" };

    public AccessLogTests() => _gate = new AccessLogRetentionGate(_clock);

    public void Dispose() => _database.Dispose();

    private async Task RecordAsync(string kind, string? user = "Panel", string? path = "/courses", DateTime? at = null, string? userAgent = null)
    {
        await using var db = _database.CreateContext();
        var service = new AccessLogService(db, _gate, Options.Create(_options), _clock, NullLogger<AccessLogService>.Instance);
        await service.RecordAsync(new AccessLogEntry
        {
            Kind = kind,
            UserName = user,
            Path = path,
            IpPrefix = IpAnonymiser.Prefix("81.2.69.142"),
            UserAgent = userAgent,
            OccurredUtc = at ?? _clock.Now.UtcDateTime
        });
    }

    [Theory]
    [InlineData("81.2.69.142", "81.2.69.x")]
    [InlineData(" 10.0.0.7 ", "10.0.0.x")]
    [InlineData("::ffff:81.2.69.142", "81.2.69.x")]
    [InlineData("2001:db8:85a3:8d3:1319:8a2e:370:7348", "2001:db8:85a3:x")]
    [InlineData("2001:DB8::1", "2001:db8:0:x")]
    public void IP_addresses_are_shortened_to_a_prefix(string ip, string expected) =>
        Assert.Equal(expected, IpAnonymiser.Prefix(ip));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip")]
    public void Anything_that_is_not_an_IP_has_no_prefix(string? ip) => Assert.Null(IpAnonymiser.Prefix(ip));

    [Fact]
    public void An_IPAddress_object_is_shortened_the_same_way() =>
        Assert.Equal("81.2.69.x", IpAnonymiser.Prefix(IPAddress.Parse("81.2.69.142")));

    [Fact]
    public async Task Entries_older_than_90_days_are_deleted_when_something_is_recorded()
    {
        await using (var db = _database.CreateContext())
        {
            db.AccessLog.AddRange(
                new AccessLogEntry { Id = Guid.NewGuid(), Kind = AccessKinds.PageView, UserName = "Old", OccurredUtc = _clock.Now.UtcDateTime.AddDays(-91) },
                new AccessLogEntry { Id = Guid.NewGuid(), Kind = AccessKinds.PageView, UserName = "Recent", OccurredUtc = _clock.Now.UtcDateTime.AddDays(-89) });
            await db.SaveChangesAsync();
        }

        await RecordAsync(AccessKinds.PageView, user: "New");

        await using var check = _database.CreateContext();
        Assert.Equal(["New", "Recent"], (await check.AccessLog.Select(e => e.UserName!).ToListAsync()).Order());
    }

    [Fact]
    public async Task The_clean_up_runs_at_most_once_an_hour()
    {
        await RecordAsync(AccessKinds.PageView, user: "First");
        await using (var db = _database.CreateContext())
        {
            db.AccessLog.Add(new AccessLogEntry { Id = Guid.NewGuid(), Kind = AccessKinds.PageView, UserName = "Stale", OccurredUtc = _clock.Now.UtcDateTime.AddDays(-200) });
            await db.SaveChangesAsync();
        }

        await RecordAsync(AccessKinds.PageView, user: "Second");
        await using (var soon = _database.CreateContext())
        {
            Assert.Contains("Stale", await soon.AccessLog.Select(e => e.UserName!).ToListAsync());
        }

        _clock.Advance(TimeSpan.FromMinutes(61));
        await RecordAsync(AccessKinds.PageView, user: "Third");

        await using var later = _database.CreateContext();
        Assert.DoesNotContain("Stale", await later.AccessLog.Select(e => e.UserName!).ToListAsync());
    }

    [Fact]
    public async Task Only_the_first_page_view_of_the_UK_day_queues_a_notification()
    {
        await RecordAsync(AccessKinds.PageView, path: "/");
        await RecordAsync(AccessKinds.PageView, path: "/courses");
        await RecordAsync(AccessKinds.PageView, path: "/events");
        await RecordAsync(AccessKinds.PageView, user: "Other", path: "/");

        await using var db = _database.CreateContext();
        var emails = await db.OutboxEmails.OrderBy(e => e.CorrelationKey).ToListAsync();
        Assert.Equal(["access:Other:2026-10-01", "access:Panel:2026-10-01"], emails.Select(e => e.CorrelationKey));
        var first = emails.Single(e => e.CorrelationKey == "access:Panel:2026-10-01");
        Assert.Equal("mason@example.com", first.To);
        Assert.Equal("Panel viewed the Chelmsford demo", first.Subject);
        Assert.Contains("1 October 2026 at 10am", first.Body, StringComparison.Ordinal);
        Assert.Contains("81.2.69.x", first.Body, StringComparison.Ordinal);
        Assert.Contains("Page: /", first.Body, StringComparison.Ordinal);
        Assert.Equal(4, await db.AccessLog.CountAsync());
    }

    [Fact]
    public async Task The_next_UK_day_notifies_again_even_before_midnight_UTC()
    {
        // 22:30 UTC is 23:30 BST on 1 October; 23:30 UTC is 00:30 BST on 2 October.
        await RecordAsync(AccessKinds.PageView, at: new DateTime(2026, 10, 1, 22, 30, 0, DateTimeKind.Utc));
        await RecordAsync(AccessKinds.PageView, at: new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc));
        await RecordAsync(AccessKinds.PageView, at: new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));

        await using var db = _database.CreateContext();
        Assert.Equal(
            ["access:Panel:2026-10-01", "access:Panel:2026-10-02"],
            (await db.OutboxEmails.Select(e => e.CorrelationKey).ToListAsync()).Order());
    }

    [Fact]
    public async Task Every_CMS_login_notifies_but_failed_logins_and_logouts_do_not()
    {
        await RecordAsync(AccessKinds.CmsLogin, path: "/umbraco");
        await RecordAsync(AccessKinds.CmsLogin, path: "/umbraco");
        await RecordAsync(AccessKinds.CmsLoginFailed, path: "/umbraco");
        await RecordAsync(AccessKinds.CmsLogout, path: "/umbraco");

        await using var db = _database.CreateContext();
        var emails = await db.OutboxEmails.ToListAsync();
        var loginIds = (await db.AccessLog.Where(e => e.Kind == AccessKinds.CmsLogin).Select(e => e.Id).ToListAsync()).Select(id => $"login:{id:D}");
        Assert.Equal(loginIds.Order(), emails.Select(e => e.CorrelationKey).Order());
        Assert.All(emails, e => Assert.Equal("Panel signed in to the Chelmsford demo CMS", e.Subject));
        Assert.Equal(4, await db.AccessLog.CountAsync());
    }

    [Fact]
    public async Task No_notify_address_means_no_emails()
    {
        _options.NotifyEmail = "  ";

        await RecordAsync(AccessKinds.PageView);
        await RecordAsync(AccessKinds.CmsLogin);

        await using var db = _database.CreateContext();
        Assert.Empty(await db.OutboxEmails.ToListAsync());
        Assert.Equal(2, await db.AccessLog.CountAsync());
    }

    [Fact]
    public async Task A_page_view_with_no_user_is_logged_without_a_notification()
    {
        await RecordAsync(AccessKinds.PageView, user: null);

        await using var db = _database.CreateContext();
        Assert.Empty(await db.OutboxEmails.ToListAsync());
        Assert.Single(await db.AccessLog.ToListAsync());
    }

    [Fact]
    public async Task User_agents_are_truncated_and_unknown_kinds_are_refused()
    {
        await RecordAsync(AccessKinds.PageView, userAgent: new string('x', 500));

        await using var db = _database.CreateContext();
        Assert.Equal(200, (await db.AccessLog.SingleAsync()).UserAgent!.Length);
        await Assert.ThrowsAsync<ArgumentException>(() => RecordAsync("something-else"));
    }

    [Fact]
    public async Task Recent_entries_come_back_newest_first_and_can_be_filtered_by_kind()
    {
        await RecordAsync(AccessKinds.PageView, path: "/a");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RecordAsync(AccessKinds.CmsLogin, path: "/umbraco");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RecordAsync(AccessKinds.PageView, path: "/b");

        await using var db = _database.CreateContext();
        var log = new AccessLogService(db, _gate, Options.Create(_options), _clock, NullLogger<AccessLogService>.Instance);

        Assert.Equal(["/b", "/umbraco", "/a"], (await log.RecentAsync(10)).Select(e => e.Path));
        Assert.Equal(["/b", "/a"], (await log.RecentAsync(10, AccessKinds.PageView)).Select(e => e.Path));
        Assert.Single(await log.RecentAsync(1));
    }

    [Fact]
    public async Task The_daily_summary_counts_page_views_per_user_per_UK_day()
    {
        await RecordAsync(AccessKinds.PageView, at: new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc));
        await RecordAsync(AccessKinds.PageView, at: new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        await RecordAsync(AccessKinds.PageView, at: new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc));
        await RecordAsync(AccessKinds.PageView, user: "Other", at: new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        await RecordAsync(AccessKinds.CmsLogin, at: new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));

        await using var db = _database.CreateContext();
        var log = new AccessLogService(db, _gate, Options.Create(_options), _clock, NullLogger<AccessLogService>.Instance);

        var twoDays = await log.DailySummaryAsync(2);
        var oneDay = await log.DailySummaryAsync(1);

        Assert.Collection(
            twoDays,
            other => Assert.Equal(("Other", 1), (other.UserName, other.PageViews)),
            panel =>
            {
                Assert.Equal(new DateOnly(2026, 10, 1), panel.Day);
                Assert.Equal(("Panel", 2), (panel.UserName, panel.PageViews));
                Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), panel.FirstSeenUtc);
                Assert.Equal(new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc), panel.LastSeenUtc);
            },
            yesterday => Assert.Equal(new DateOnly(2026, 9, 30), yesterday.Day));
        Assert.Equal(2, oneDay.Count);
    }
}
