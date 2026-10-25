using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>
/// Receives Stripe webhooks. The signature is verified first, then the event ID is recorded before any
/// processing, so a redelivered event is recognised and never processed twice.
/// </summary>
public sealed class StripeWebhookHandler(
    SiteDbContext db,
    IPaymentService payments,
    IOptions<StripeOptions> options,
    TimeProvider clock,
    ILogger<StripeWebhookHandler> logger) : IPaymentWebhookHandler
{
    internal const string CheckoutSessionCompleted = "checkout.session.completed";

    public async Task<WebhookOutcome> HandleAsync(string payload, string? signatureHeader, CancellationToken cancellationToken = default)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader) || string.IsNullOrEmpty(payload))
        {
            logger.LogWarning("Stripe webhook rejected: no webhook secret configured or no signature supplied");
            return WebhookOutcome.InvalidSignature;
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signatureHeader, secret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(ex, "Stripe webhook rejected: signature verification failed");
            return WebhookOutcome.InvalidSignature;
        }

        var session = stripeEvent.Data?.Object as Session;
        var notification = new WebhookNotification(
            stripeEvent.Id,
            stripeEvent.Type,
            session?.PaymentStatus,
            session?.Metadata is { } metadata && metadata.TryGetValue("order_id", out var orderId) ? orderId : null,
            session?.PaymentIntentId);

        return await ProcessAsync(notification, cancellationToken);
    }

    /// <summary>Processes a verified event. Separate from signature checking so the dedupe logic can be tested directly.</summary>
    internal async Task<WebhookOutcome> ProcessAsync(WebhookNotification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // Record the event first: if two deliveries race, the primary key lets exactly one through.
        db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
        {
            EventId = notification.EventId,
            Type = notification.Type,
            ReceivedUtc = clock.GetUtcNow().UtcDateTime
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Stripe event {EventId} already processed; ignoring the repeat", notification.EventId);
            return WebhookOutcome.Duplicate;
        }

        if (notification.Type != CheckoutSessionCompleted || notification.PaymentStatus != "paid")
        {
            logger.LogDebug("Stripe event {EventId} ({Type}) needs no action", notification.EventId, notification.Type);
            return WebhookOutcome.Ignored;
        }

        if (!Guid.TryParse(notification.OrderId, out var orderId))
        {
            logger.LogWarning("Stripe event {EventId} has no valid order_id in its metadata", notification.EventId);
            return WebhookOutcome.Ignored;
        }

        try
        {
            var order = await payments.MarkPaidAsync(orderId, notification.PaymentIntentId, cancellationToken);
            if (order is null)
            {
                logger.LogWarning("Stripe event {EventId} refers to unknown order {OrderId}", notification.EventId, orderId);
                return WebhookOutcome.Ignored;
            }
        }
        catch
        {
            // Let Stripe retry: forget the event so the redelivery is processed rather than treated as a duplicate.
            await ForgetAsync(notification.EventId, cancellationToken);
            throw;
        }

        return WebhookOutcome.Processed;
    }

    private async Task ForgetAsync(string eventId, CancellationToken cancellationToken)
    {
        try
        {
            db.ChangeTracker.Clear();
            await db.ProcessedWebhookEvents.Where(e => e.EventId == eventId).ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not clear the record of failed Stripe event {EventId}", eventId);
        }
    }
}

/// <summary>The parts of a verified provider event that payment processing needs.</summary>
internal sealed record WebhookNotification(string EventId, string Type, string? PaymentStatus, string? OrderId, string? PaymentIntentId);
