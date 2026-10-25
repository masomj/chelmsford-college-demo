using System.Globalization;
using System.Text;
using Chelmsford.Core.Common;
using Chelmsford.Core.Messaging;
using Chelmsford.Core.Payments;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>Writes the emails that are queued in the outbox when an order is paid.</summary>
public interface IReceiptEmailComposer
{
    /// <summary>The payer's receipt. Times in it are UK time.</summary>
    OutboxEmail Compose(PaymentOrder order, string? subjectEmail, DateTime createdUtc);

    /// <summary>Tells finance that a payment was taken twice for the same item and the later one needs refunding.</summary>
    OutboxEmail ComposeDuplicatePaymentNotice(PaymentOrder order, string financeEmail, DateTime createdUtc);
}

public sealed class ReceiptEmailComposer : IReceiptEmailComposer
{
    private static readonly CultureInfo UkCulture = CultureInfo.GetCultureInfo("en-GB");

    public static string CorrelationKey(Guid orderId) => $"receipt:{orderId:D}";

    public static string DuplicateCorrelationKey(Guid orderId) => $"duplicate:{orderId:D}";

    public OutboxEmail ComposeDuplicatePaymentNotice(PaymentOrder order, string financeEmail, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(financeEmail);

        var received = UkTime.FormatUtc(order.PaidUtc ?? createdUtc);
        var body = new StringBuilder()
            .AppendLine("A payment has been taken twice for the same item and the later payment needs refunding.")
            .AppendLine()
            .Append("Order reference: ").AppendLine(order.Reference)
            .Append("Payment provider: ").AppendLine(order.Gateway)
            .Append("Payment ID: ").AppendLine(string.IsNullOrWhiteSpace(order.GatewayPaymentId) ? "(not given)" : order.GatewayPaymentId)
            .Append("Amount to refund: ").AppendLine(FormatPounds(order.AmountPence))
            .Append("Payment for: ").AppendLine(order.ItemName)
            .Append("Cost code: ").AppendLine(string.IsNullOrWhiteSpace(order.CostCode) ? "(none)" : order.CostCode)
            .Append(order.SubjectKind == PaymentSubjectKind.Student ? "Student ID: " : "Application reference: ").AppendLine(order.SubjectReference)
            .Append("Student: ").AppendLine(order.SubjectFirstName)
            .Append("Paid by: ").Append(order.PayerName).Append(" <").Append(order.PayerEmail).AppendLine(">")
            .Append("Received: ").AppendLine(received)
            .AppendLine()
            .AppendLine("This order had expired or been cancelled when the payment arrived, and the same item has already been paid for on another order. The payer has not been sent a receipt for this payment.")
            .ToString();

        return new OutboxEmail
        {
            Id = Guid.NewGuid(),
            CorrelationKey = DuplicateCorrelationKey(order.Id),
            To = financeEmail.Trim(),
            Subject = $"Duplicate payment to refund: {order.Reference} ({FormatPounds(order.AmountPence)})",
            Body = body,
            CreatedUtc = createdUtc
        };
    }

    public static string FormatPounds(int pence) => (pence / 100m).ToString("C", UkCulture);

    public OutboxEmail Compose(PaymentOrder order, string? subjectEmail, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(order);

        var payerIsSubject = order.PayerType is PayerType.Student or PayerType.Applicant;
        var cc = !payerIsSubject
                 && !string.IsNullOrWhiteSpace(subjectEmail)
                 && !string.Equals(subjectEmail, order.PayerEmail, StringComparison.OrdinalIgnoreCase)
            ? subjectEmail
            : null;

        var amount = FormatPounds(order.AmountPence);
        var paidOn = UkTime.FormatUtc(order.PaidUtc ?? createdUtc);
        var body = new StringBuilder()
            .Append("Dear ").Append(order.PayerName).AppendLine(",")
            .AppendLine()
            .AppendLine("Thank you. Your payment to Chelmsford College has been received.")
            .AppendLine()
            .Append("Reference: ").AppendLine(order.Reference)
            .Append("Payment for: ").AppendLine(order.ItemName)
            .Append("Amount paid: ").AppendLine(amount)
            .Append("Student: ").AppendLine(order.SubjectFirstName)
            .Append("Course: ").AppendLine(order.CourseTitle.Length > 0 ? order.CourseTitle : order.CourseCode ?? string.Empty)
            .Append("Paid on: ").AppendLine(paidOn)
            .AppendLine()
            .AppendLine("Please keep this email as your receipt. If you have any questions, reply quoting your reference.")
            .AppendLine()
            .AppendLine("Chelmsford College Finance Team")
            .ToString();

        return new OutboxEmail
        {
            Id = Guid.NewGuid(),
            CorrelationKey = CorrelationKey(order.Id),
            To = order.PayerEmail,
            Cc = cc,
            Subject = $"Payment received: {order.ItemName} ({order.Reference})",
            Body = body,
            CreatedUtc = createdUtc
        };
    }
}
