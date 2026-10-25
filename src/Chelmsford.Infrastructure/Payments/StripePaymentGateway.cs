using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>
/// Stripe hosted Checkout. Card details are entered on Stripe's page and never reach this server.
/// Every session and payment carries metadata linking it to the order, the student and the cost code.
/// </summary>
/// <remarks>
/// A session never outlives its order's hold by more than Stripe forces it to (its minimum is 30 minutes),
/// an order that is checked out again reuses its session while it is still open and still matches the order,
/// and a Stripe error reaches callers as a <see cref="PaymentGatewayException"/> with a message safe to show.
/// </remarks>
public sealed class StripePaymentGateway(
    IStripeClient stripeClient,
    IOptions<StripeOptions> options,
    IOptions<PaymentOptions> paymentOptions,
    TimeProvider clock,
    ILogger<StripePaymentGateway> logger) : IPaymentGateway
{
    public const string GatewayName = "Stripe";
    private const string Currency = "gbp";

    /// <summary>Stripe will not create a session that expires sooner than this.</summary>
    internal const int MinimumSessionMinutes = 30;

    /// <summary>Stripe will not create a session that lasts longer than this (24 hours).</summary>
    internal const int MaximumSessionMinutes = 24 * 60;

    /// <summary>
    /// Stripe checks the minimum against its own clock when the request arrives, so asking for exactly 30
    /// minutes can be refused by a few hundred milliseconds. This margin keeps the request valid.
    /// </summary>
    internal static readonly TimeSpan NetworkMargin = TimeSpan.FromMinutes(1);

    public string Name => GatewayName;

    public bool IsTestMode => options.Value.SecretKey?.StartsWith("sk_test_", StringComparison.Ordinal) == true;

    public async Task<GatewayCheckout> CreateCheckoutAsync(PaymentOrder order, CheckoutUrls urls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(urls);

        var service = new SessionService(stripeClient);
        try
        {
            if (!string.IsNullOrWhiteSpace(order.GatewaySessionId)
                && await ReuseOrRetireAsync(service, order, cancellationToken) is { } reusable)
            {
                logger.LogInformation("Reusing open Stripe Checkout session {SessionId} for order {Reference}", reusable.Id, order.Reference);
                return new GatewayCheckout(reusable.Id, reusable.Url);
            }

            var createOptions = BuildCreateOptions(order, urls, SessionExpiresAt(clock.GetUtcNow(), paymentOptions.Value.PendingOrderMinutes));
            var requestOptions = new RequestOptions { IdempotencyKey = IdempotencyKey(order) };
            var session = await service.CreateAsync(createOptions, requestOptions, cancellationToken);

            logger.LogInformation("Stripe Checkout session {SessionId} created for order {Reference}", session.Id, order.Reference);
            return new GatewayCheckout(session.Id, session.Url);
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Stripe refused the checkout for order {Reference}: {Code}", order.Reference, ex.StripeError?.Code);
            throw new PaymentGatewayException(PaymentGatewayException.DefaultMessage, ex);
        }
    }

    public async Task<GatewayPaymentStatus> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        try
        {
            var service = new SessionService(stripeClient);
            var session = await service.GetAsync(sessionId, options: null, requestOptions: null, cancellationToken);
            return ToStatus(session.PaymentStatus, session.Status, session.PaymentIntentId);
        }
        catch (StripeException ex)
        {
            logger.LogError(ex, "Could not read Stripe Checkout session {SessionId}: {Code}", sessionId, ex.StripeError?.Code);
            throw new PaymentGatewayException(PaymentGatewayException.DefaultMessage, ex);
        }
    }

    public async Task ExpireAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            await new SessionService(stripeClient).ExpireAsync(sessionId, options: null, requestOptions: null, cancellationToken);
            logger.LogInformation("Stripe Checkout session {SessionId} expired", sessionId);
        }
        catch (StripeException ex)
        {
            // Most often the session was already paid, expired or closed, which is exactly what we wanted or can't change.
            logger.LogWarning(ex, "Could not expire Stripe Checkout session {SessionId}: {Code}", sessionId, ex.StripeError?.Code);
        }
    }

    /// <summary>
    /// When the checkout session should expire: the order's hold, but never less than Stripe's 30-minute minimum
    /// or more than its 24-hour maximum, so a session never outlives its order by more than it must.
    /// </summary>
    internal static DateTime SessionExpiresAt(DateTimeOffset now, int pendingOrderMinutes) =>
        now.UtcDateTime
            .AddMinutes(Math.Clamp(pendingOrderMinutes, MinimumSessionMinutes, MaximumSessionMinutes - (int)NetworkMargin.TotalMinutes))
            .Add(NetworkMargin);

    /// <summary>
    /// The same order, amount and payer always map to the same Stripe request, so a retried call can't create a
    /// second session; changing the amount or payer's email, or replacing an earlier session, gives a new key.
    /// </summary>
    internal static string IdempotencyKey(PaymentOrder order)
    {
        var fingerprint = string.Create(
            CultureInfo.InvariantCulture,
            $"{order.AmountPence}|{order.PayerEmail.Trim().ToLowerInvariant()}|{order.GatewaySessionId}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))[..12];
        return $"checkout-{order.Id:D}-{hash}";
    }

    internal static GatewayPaymentStatus ToStatus(string? paymentStatus, string? sessionStatus, string? paymentIntentId) =>
        paymentStatus switch
        {
            "paid" or "no_payment_required" => new GatewayPaymentStatus(GatewayPaymentState.Paid, paymentIntentId),
            _ when sessionStatus == "expired" => new GatewayPaymentStatus(GatewayPaymentState.Failed, null),
            _ => new GatewayPaymentStatus(GatewayPaymentState.Pending, null)
        };

    internal static Dictionary<string, string> Metadata(PaymentOrder order) => new(StringComparer.Ordinal)
    {
        ["order_id"] = order.Id.ToString("D"),
        ["order_reference"] = order.Reference,
        ["subject_reference"] = order.SubjectReference,
        ["subject_kind"] = order.SubjectKind.ToString(),
        ["course_code"] = string.IsNullOrWhiteSpace(order.CourseCode) ? $"C{order.CourseId.ToString(CultureInfo.InvariantCulture)}" : order.CourseCode,
        ["item_key"] = order.ItemKey.ToString("D"),
        ["item_type"] = order.ItemType.ToString(),
        ["cost_code"] = order.CostCode ?? string.Empty
    };

    internal static SessionCreateOptions BuildCreateOptions(PaymentOrder order, CheckoutUrls urls, DateTime expiresAtUtc)
    {
        var metadata = Metadata(order);
        return new SessionCreateOptions
        {
            Mode = "payment",
            ClientReferenceId = order.Reference,
            CustomerEmail = order.PayerEmail,
            SuccessUrl = urls.SuccessUrl,
            CancelUrl = urls.CancelUrl,
            ExpiresAt = expiresAtUtc,
            Metadata = metadata,
            PaymentIntentData = new SessionPaymentIntentDataOptions
            {
                Metadata = new Dictionary<string, string>(metadata),
                Description = $"{order.ItemName} ({order.Reference})"
            },
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = Currency,
                        UnitAmount = order.AmountPence,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = order.ItemName,
                            Description = Describe(order)
                        }
                    }
                }
            ]
        };
    }

    /// <summary>
    /// Returns the order's earlier session if it is still open and still describes this order (same amount and
    /// payer email). Otherwise closes it, so only one live session ever exists for an order, and returns null.
    /// </summary>
    private async Task<Session?> ReuseOrRetireAsync(SessionService service, PaymentOrder order, CancellationToken cancellationToken)
    {
        Session existing;
        try
        {
            existing = await service.GetAsync(order.GatewaySessionId, options: null, requestOptions: null, cancellationToken);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            logger.LogWarning("Earlier Stripe session {SessionId} for order {Reference} no longer exists; creating a new one", order.GatewaySessionId, order.Reference);
            return null;
        }

        if (!string.Equals(existing.Status, "open", StringComparison.Ordinal))
        {
            return null;
        }

        var matches = !string.IsNullOrEmpty(existing.Url)
                      && existing.AmountTotal == order.AmountPence
                      && string.Equals(existing.CustomerEmail, order.PayerEmail, StringComparison.OrdinalIgnoreCase);
        if (matches)
        {
            return existing;
        }

        await ExpireAsync(existing.Id, cancellationToken);
        return null;
    }

    private static string Describe(PaymentOrder order)
    {
        var course = order.CourseTitle.Length > 0 ? order.CourseTitle : order.CourseCode;
        return string.IsNullOrWhiteSpace(course)
            ? string.Create(CultureInfo.InvariantCulture, $"For {order.SubjectFirstName}")
            : string.Create(CultureInfo.InvariantCulture, $"For {order.SubjectFirstName} · {course}");
    }
}
