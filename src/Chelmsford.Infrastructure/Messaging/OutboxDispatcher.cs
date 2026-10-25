using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using Chelmsford.Core.Integration;
using Chelmsford.Core.Messaging;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Messaging;

/// <summary>Delivers one queued email. Kept behind an interface so the outbox logic doesn't depend on SMTP.</summary>
public interface IOutboxMailSender
{
    Task SendAsync(OutboxEmail email, CancellationToken cancellationToken = default);
}

/// <summary>Sends outbox emails through the SMTP server in the "Smtp" settings.</summary>
public sealed class SmtpOutboxMailSender(IOptions<SmtpOptions> options) : IOutboxMailSender
{
    public async Task SendAsync(OutboxEmail email, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);

        var smtp = options.Value;
        var from = !string.IsNullOrWhiteSpace(smtp.FromAddress) ? smtp.FromAddress : smtp.UserName;
        if (string.IsNullOrWhiteSpace(from))
        {
            throw new InvalidOperationException("Smtp:FromAddress (or Smtp:UserName) must be set to send email.");
        }

        using var message = new MailMessage { From = new MailAddress(from, smtp.FromName), Subject = email.Subject, Body = email.Body, IsBodyHtml = false };
        AddAddresses(message.To, email.To);
        AddAddresses(message.CC, email.Cc);

        using var client = new SmtpClient(smtp.Host, smtp.Port) { EnableSsl = smtp.EnableSsl };
        if (!string.IsNullOrWhiteSpace(smtp.UserName))
        {
            client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }

    private static void AddAddresses(MailAddressCollection target, string? addresses)
    {
        foreach (var address in (addresses ?? string.Empty).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            target.Add(address);
        }
    }
}

/// <summary>
/// Remembers, in memory, which emails failed to send and when to try them again: 30 seconds, then doubling up to an
/// hour. Restarting the site starts the backoff afresh, which is fine because nothing is lost: unsent emails stay in the outbox.
/// </summary>
public sealed class OutboxRetryState
{
    internal static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, (int Failures, DateTimeOffset RetryAt)> _failures = new();

    public bool IsWaiting(Guid emailId, DateTimeOffset now) =>
        _failures.TryGetValue(emailId, out var state) && state.RetryAt > now;

    /// <summary>Notes a failure and returns how long to wait before the next try.</summary>
    public TimeSpan RecordFailure(Guid emailId, DateTimeOffset now)
    {
        var state = _failures.AddOrUpdate(
            emailId,
            _ => (1, now + FirstDelay),
            (_, previous) => (previous.Failures + 1, now + DelayAfter(previous.Failures + 1)));
        return state.RetryAt - now;
    }

    public void Clear(Guid emailId) => _failures.TryRemove(emailId, out _);

    internal static TimeSpan DelayAfter(int failures)
    {
        var seconds = FirstDelay.TotalSeconds * Math.Pow(2, Math.Min(Math.Max(failures, 1) - 1, 20));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxDelay.TotalSeconds));
    }
}

/// <summary>Sends the emails waiting in the outbox. Does nothing unless <c>Smtp:Host</c> is set.</summary>
public sealed class OutboxProcessor(
    SiteDbContext db,
    IOutboxMailSender sender,
    OutboxRetryState retry,
    IOptions<SmtpOptions> options,
    TimeProvider clock,
    ILogger<OutboxProcessor> logger)
{
    internal const int BatchSize = 25;

    /// <summary>Sends each unsent email once (skipping any still waiting to retry) and returns how many were sent.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.IsConfigured)
        {
            return 0;
        }

        var pending = await db.OutboxEmails
            .Where(e => e.SentUtc == null)
            .OrderBy(e => e.CreatedUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var email in pending)
        {
            if (retry.IsWaiting(email.Id, clock.GetUtcNow()))
            {
                continue;
            }

            try
            {
                await sender.SendAsync(email, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var wait = retry.RecordFailure(email.Id, clock.GetUtcNow());
                logger.LogWarning(ex, "Could not send email {CorrelationKey}; trying again in {Wait}", email.CorrelationKey, wait);
                continue;
            }

            email.SentUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            retry.Clear(email.Id);
            sent++;
            logger.LogInformation("Sent email {CorrelationKey}", email.CorrelationKey);
        }

        return sent;
    }
}

/// <summary>Checks the outbox every 30 seconds once the site is ready, if SMTP is configured.</summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    ISiteReadiness readiness,
    IOptions<SmtpOptions> options,
    TimeProvider clock,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
        {
            logger.LogInformation("Smtp:Host is not set; queued emails will not be sent");
            return;
        }

        try
        {
            await readiness.WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        logger.LogInformation("Outbox dispatcher started; sending through {Host}:{Port} every {Interval}", options.Value.Host, options.Value.Port, Interval);

        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await WaitForNextTickAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessPendingAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Outbox dispatch threw; will retry on the next tick");
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
