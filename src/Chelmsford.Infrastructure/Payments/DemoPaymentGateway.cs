using Chelmsford.Core.Payments;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>
/// Used when no Stripe keys are configured. Sends the payer to the website's own clearly labelled
/// test checkout page, which records the payment itself; no money moves.
/// </summary>
public sealed class DemoPaymentGateway : IPaymentGateway
{
    public const string GatewayName = "Demo";
    public const string SessionPrefix = "demo_";

    public string Name => GatewayName;

    public bool IsTestMode => true;

    public Task<GatewayCheckout> CreateCheckoutAsync(PaymentOrder order, CheckoutUrls urls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        // The test checkout page is addressed by order reference, so going back to it reuses the order's session.
        var sessionId = order.GatewaySessionId is { Length: > 0 } existing && existing.StartsWith(SessionPrefix, StringComparison.Ordinal)
            ? existing
            : SessionPrefix + Guid.NewGuid().ToString("N");
        var checkout = new GatewayCheckout(sessionId, "/pay/test-checkout/" + Uri.EscapeDataString(order.Reference));
        return Task.FromResult(checkout);
    }

    /// <summary>There is no provider session to close.</summary>
    public Task ExpireAsync(string sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>The test checkout page marks the order paid directly, so the provider status is always pending.</summary>
    public Task<GatewayPaymentStatus> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GatewayPaymentStatus(GatewayPaymentState.Pending, null));
}
