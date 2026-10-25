namespace Chelmsford.Core.Payments;

public enum PayableItemType
{
    CourseDeposit = 0,
    MaterialsFee = 1,
    Trip = 2,
    OneOff = 3
}

public enum PaymentSubjectKind
{
    Student = 0,
    Applicant = 1
}

public enum PayerType
{
    Student = 0,
    Applicant = 1,
    ParentOrCarer = 2,
    Other = 3
}

public enum OrderStatus
{
    Pending = 0,
    Paid = 1,
    Cancelled = 2,
    Expired = 3,
    Refunded = 4,

    /// <summary>
    /// The provider took a payment for an order that had already expired or been cancelled, and the same
    /// subject has since paid for the same item. The money was taken, so it is recorded, but it needs refunding.
    /// </summary>
    PaidDuplicate = 5
}

/// <summary>
/// A payable item created by finance or curriculum staff in the CMS: a course deposit,
/// materials fee, trip or one-off payment, linked to one or more courses.
/// </summary>
public sealed record PayableItem(
    Guid Key,
    string Name,
    PayableItemType Type,
    string? Description,
    int AmountPence,
    IReadOnlyList<int> CourseIds,
    DateTime? DueDate,
    int? Places,
    string? CostCode,
    DateTime? TakesPlaceOn)
{
    public static string TypeLabel(PayableItemType type) => type switch
    {
        PayableItemType.CourseDeposit => "Course deposit",
        PayableItemType.MaterialsFee => "Materials fee",
        PayableItemType.Trip => "Trip",
        _ => "One-off payment"
    };

    public string TypeName => TypeLabel(Type);

    /// <summary>Deposits are paid by applicants before enrolment; everything else by enrolled students.</summary>
    public bool AppliesTo(PaymentSubjectKind kind) =>
        Type == PayableItemType.CourseDeposit ? kind == PaymentSubjectKind.Applicant : kind == PaymentSubjectKind.Student;
}

/// <summary>Who a payment is for: an enrolled student (by student ID) or an applicant (by application reference).</summary>
public sealed record PaymentSubject(
    PaymentSubjectKind Kind,
    string Reference,
    string FirstName,
    string Email,
    int CourseId,
    string CourseTitle,
    string? CourseCode);

/// <summary>Who is paying: the student, or a parent, carer or other third party on their behalf.</summary>
public sealed record Payer(PayerType Type, string Name, string Email, string? Relationship);

public sealed record DueItem(PayableItem Item, int? PlacesLeft, bool AlreadyPaid)
{
    public bool SoldOut => PlacesLeft is <= 0;

    /// <summary>True once the item's due date (end of that day) or the day it takes place has passed, in UK time.</summary>
    public bool Closed { get; init; }

    public bool CanPay => !AlreadyPaid && !SoldOut && !Closed;
}

/// <summary>An order is created before the payer is sent to checkout, so every payment is processed exactly once.</summary>
public sealed class PaymentOrder
{
    public Guid Id { get; set; }

    /// <summary>Human-friendly reference shown to the payer and on receipts.</summary>
    public string Reference { get; set; } = string.Empty;

    public PaymentSubjectKind SubjectKind { get; set; }
    public string SubjectReference { get; set; } = string.Empty;
    public string SubjectFirstName { get; set; } = string.Empty;
    public int CourseId { get; set; }
    public string CourseTitle { get; set; } = string.Empty;
    public string? CourseCode { get; set; }

    public Guid ItemKey { get; set; }
    public string ItemName { get; set; } = string.Empty;
    public PayableItemType ItemType { get; set; }
    public string? CostCode { get; set; }
    public int AmountPence { get; set; }

    public PayerType PayerType { get; set; }
    public string PayerName { get; set; } = string.Empty;
    public string PayerEmail { get; set; } = string.Empty;
    public string? PayerRelationship { get; set; }

    public OrderStatus Status { get; set; }
    public string Gateway { get; set; } = string.Empty;
    public string? GatewaySessionId { get; set; }
    public string? GatewayPaymentId { get; set; }
    public Guid? PaymentLinkId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? PaidUtc { get; set; }

    /// <summary>Set while the order is Pending or Paid; null otherwise. A unique index on (ItemKey, SubjectReference, ActiveMarker) stops duplicates.</summary>
    public string? ActiveMarker { get; set; }
}

/// <summary>A unique, secure, single-use link for one subject and one item, which expires.</summary>
public sealed class PaymentLink
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public PaymentSubjectKind SubjectKind { get; set; }
    public string SubjectReference { get; set; } = string.Empty;
    public Guid ItemKey { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

/// <summary>Records each payment-provider event once, so a repeated webhook is never processed twice.</summary>
public sealed class ProcessedWebhookEvent
{
    public string EventId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; }
}
