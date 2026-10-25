using Chelmsford.Core.Payments;

namespace Chelmsford.Web.Services;

/// <summary>Display rules for payable items that the course page needs before any payment is started.</summary>
public static class PayableItemRules
{
    /// <summary>
    /// A payment is closed once its due date has passed (the whole of the due day still counts) or the
    /// trip has taken place. Times are UK local time, as editors enter them.
    /// </summary>
    public static bool IsClosed(PayableItem item, DateTime nowLocal)
    {
        if (item.DueDate is { } due)
        {
            var closesAt = due.TimeOfDay == TimeSpan.Zero ? due.Date.AddDays(1) : due;
            if (nowLocal >= closesAt)
            {
                return true;
            }
        }

        return item.TakesPlaceOn is { } takesPlace && nowLocal >= takesPlace;
    }
}
