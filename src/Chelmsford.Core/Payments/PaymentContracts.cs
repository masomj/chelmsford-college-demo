namespace Chelmsford.Core.Payments;

/// <summary>Payable items as managed by staff in the CMS.</summary>
public interface IPayableItemCatalogue
{
    Task<IReadOnlyList<PayableItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<PayableItem?> GetAsync(Guid key, CancellationToken cancellationToken = default);
}

public enum GuestLookupOutcome
{
    Found,
    NotFound,
    TooManyAttempts
}

public sealed record GuestLookupResult(GuestLookupOutcome Outcome, PaymentSubject? Subject, int AttemptsLeft);

public enum StartOrderOutcome
{
    Started,
    AlreadyPaid,
    SoldOut,
    NotAvailable
}

public sealed record StartOrderResult(StartOrderOutcome Outcome, PaymentOrder? Order);

public sealed record CheckoutUrls(string SuccessUrl, string CancelUrl);

public sealed record GatewayCheckout(string SessionId, string RedirectUrl);

public enum GatewayPaymentState
{
    Pending,
    Paid,
    Failed
}

public sealed record GatewayPaymentStatus(GatewayPaymentState State, string? PaymentId);

/// <summary>
/// The payment provider sits behind this interface so the college can swap Stripe for a provider it
/// already uses (WPM Education, Pay360) without changing the rest of the website.
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }

    /// <summary>True when no real money can move (Stripe test keys, or the built-in demo checkout).</summary>
    bool IsTestMode { get; }

    Task<GatewayCheckout> CreateCheckoutAsync(PaymentOrder order, CheckoutUrls urls, CancellationToken cancellationToken = default);

    Task<GatewayPaymentStatus> GetStatusAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an unpaid checkout session so it can no longer be paid (used when its order has expired).
    /// Never throws for a provider error: a session that has already been paid or closed is not a problem.
    /// </summary>
    Task ExpireAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The payment provider could not be reached or refused a request. <see cref="Exception.Message"/> is safe to
/// show to a payer; the provider's own error is kept as the inner exception for logging.
/// </summary>
public sealed class PaymentGatewayException : Exception
{
    public const string DefaultMessage = "We couldn't reach our payment provider just now. You have not been charged. Please try again in a moment.";

    public PaymentGatewayException() : base(DefaultMessage)
    {
    }

    public PaymentGatewayException(string message) : base(message)
    {
    }

    public PaymentGatewayException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}

public interface IPaymentService
{
    Task<PaymentSubject?> FindStudentAsync(string studentId, CancellationToken cancellationToken = default);
    Task<PaymentSubject?> FindSubjectAsync(PaymentSubjectKind kind, string reference, CancellationToken cancellationToken = default);
    Task<GuestLookupResult> GuestLookupAsync(string studentId, DateOnly dateOfBirth, string clientKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DueItem>> GetDueItemsAsync(PaymentSubject subject, CancellationToken cancellationToken = default);
    Task<StartOrderResult> StartOrderAsync(PaymentSubject subject, Guid itemKey, Payer payer, Guid? paymentLinkId, CancellationToken cancellationToken = default);
    Task<PaymentOrder?> GetOrderAsync(string reference, CancellationToken cancellationToken = default);
    Task<GatewayCheckout> BeginCheckoutAsync(string orderReference, CheckoutUrls urls, CancellationToken cancellationToken = default);

    /// <summary>Checks the provider when the payer returns, and records the payment if it has completed.</summary>
    Task<PaymentOrder?> ConfirmReturnAsync(string orderReference, CancellationToken cancellationToken = default);

    /// <summary>Marks an order paid. Safe to call more than once: only the first call has any effect.</summary>
    Task<PaymentOrder?> MarkPaidAsync(Guid orderId, string? gatewayPaymentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentOrder>> RecentOrdersAsync(int take, CancellationToken cancellationToken = default);
}

public sealed record ResolvedPaymentLink(PaymentLink Link, PaymentSubject Subject, PayableItem Item);

public interface IPaymentLinkService
{
    /// <summary>Creates a link and returns the raw token. Only a hash of the token is stored.</summary>
    Task<string> CreateAsync(PaymentSubjectKind kind, string subjectReference, Guid itemKey, TimeSpan validFor, CancellationToken cancellationToken = default);

    Task<ResolvedPaymentLink?> ResolveAsync(string token, CancellationToken cancellationToken = default);
}

public enum WebhookOutcome
{
    Processed,
    Duplicate,
    Ignored,
    InvalidSignature
}

public interface IPaymentWebhookHandler
{
    Task<WebhookOutcome> HandleAsync(string payload, string? signatureHeader, CancellationToken cancellationToken = default);
}
