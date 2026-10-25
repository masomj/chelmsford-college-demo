namespace Chelmsford.Core.Messaging;

/// <summary>
/// Outbox pattern: emails such as receipts are written in the same transaction as the record they
/// describe, then sent separately. A receipt is never sent for a payment that wasn't saved, and a
/// saved payment never loses its receipt.
/// </summary>
public sealed class OutboxEmail
{
    public Guid Id { get; set; }

    /// <summary>Unique per message purpose (e.g. "receipt:{orderId}"), so a message is only queued once.</summary>
    public string CorrelationKey { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;
    public string? Cc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public DateTime? SentUtc { get; set; }
}
