using Chelmsford.Core.Integration;
using Chelmsford.Core.Messaging;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Messaging;
using Chelmsford.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chelmsford.Tests;

public sealed class OutboxDispatcherTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly TestClock _clock = new();
    private readonly RecordingSender _sender = new();
    private readonly OutboxRetryState _retry = new();
    private readonly SmtpOptions _smtp = new() { Host = "smtp.example.com", FromAddress = "demo@example.com" };

    public OutboxDispatcherTests()
    {
        using var db = _database.CreateContext();
        db.OutboxEmails.AddRange(Email("a", minutesAgo: 3), Email("b", minutesAgo: 2));
        db.SaveChanges();
    }

    public void Dispose() => _database.Dispose();

    private OutboxEmail Email(string key, int minutesAgo) => new()
    {
        Id = Guid.NewGuid(), CorrelationKey = key, To = "someone@example.com", Subject = "Subject " + key, Body = "Body",
        CreatedUtc = _clock.Now.UtcDateTime.AddMinutes(-minutesAgo)
    };

    private async Task<int> ProcessAsync()
    {
        await using var db = _database.CreateContext();
        return await new OutboxProcessor(db, _sender, _retry, Options.Create(_smtp), _clock, NullLogger<OutboxProcessor>.Instance).ProcessPendingAsync();
    }

    [Fact]
    public async Task Nothing_is_sent_and_nothing_is_marked_when_no_SMTP_host_is_configured()
    {
        _smtp.Host = null;

        var sent = await ProcessAsync();

        Assert.Equal(0, sent);
        Assert.Empty(_sender.Sent);
        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.OutboxEmails.CountAsync(e => e.SentUtc == null));
    }

    [Fact]
    public async Task Unsent_emails_are_sent_oldest_first_and_marked_with_the_time()
    {
        var sent = await ProcessAsync();
        var again = await ProcessAsync();

        Assert.Equal(2, sent);
        Assert.Equal(0, again);
        Assert.Equal(["a", "b"], _sender.Sent);
        await using var db = _database.CreateContext();
        Assert.All(await db.OutboxEmails.ToListAsync(), e => Assert.Equal(_clock.Now.UtcDateTime, e.SentUtc));
    }

    [Fact]
    public async Task A_failed_email_stays_queued_and_is_retried_after_a_backoff_without_blocking_the_others()
    {
        _sender.FailKeys.Add("a");

        var first = await ProcessAsync();
        var tooSoon = await ProcessAsync();
        _sender.FailKeys.Clear();
        _clock.Advance(TimeSpan.FromSeconds(29));
        var stillWaiting = await ProcessAsync();
        _clock.Advance(TimeSpan.FromSeconds(2));
        var retried = await ProcessAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, tooSoon);
        Assert.Equal(0, stillWaiting);
        Assert.Equal(1, retried);
        Assert.Equal(["a", "b", "a"], _sender.Attempts);
        await using var db = _database.CreateContext();
        Assert.Equal(0, await db.OutboxEmails.CountAsync(e => e.SentUtc == null));
    }

    [Fact]
    public void The_retry_delay_doubles_from_30_seconds_up_to_an_hour()
    {
        var id = Guid.NewGuid();
        var now = _clock.Now;

        var delays = Enumerable.Range(0, 10).Select(_ => _retry.RecordFailure(id, now)).ToList();

        Assert.Equal(TimeSpan.FromSeconds(30), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(120), delays[2]);
        Assert.Equal(TimeSpan.FromHours(1), delays[^1]);
        _retry.Clear(id);
        Assert.False(_retry.IsWaiting(id, now));
    }

    [Fact]
    public async Task The_background_service_does_nothing_without_an_SMTP_host()
    {
        _smtp.Host = " ";
        var services = new ServiceCollection();
        services.AddSingleton<ISiteReadiness>(new SiteReadiness());
        using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<ISiteReadiness>(),
            Options.Create(_smtp),
            _clock,
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.StartAsync(CancellationToken.None);
        await (dispatcher.ExecuteTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.Empty(_sender.Attempts);
    }

    private sealed class RecordingSender : IOutboxMailSender
    {
        public List<string> Sent { get; } = [];
        public List<string> Attempts { get; } = [];
        public HashSet<string> FailKeys { get; } = [];

        public Task SendAsync(OutboxEmail email, CancellationToken cancellationToken = default)
        {
            Attempts.Add(email.CorrelationKey);
            if (FailKeys.Contains(email.CorrelationKey))
            {
                throw new InvalidOperationException("SMTP down");
            }

            Sent.Add(email.CorrelationKey);
            return Task.CompletedTask;
        }
    }
}
