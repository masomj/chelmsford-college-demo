using Chelmsford.Core.Common;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chelmsford.Infrastructure.Payments;

/// <summary>
/// Payment journeys: finding who a payment is for, what they owe, and taking each payment exactly once.
/// </summary>
/// <remarks>
/// Exactly-once rests on three things: an order is created before checkout and reused on refresh;
/// a unique index on (item, subject, active marker) means two live orders for the same thing can't
/// exist even under a race; and marking an order paid is idempotent, with the receipt queued in the
/// same save as the payment.
/// </remarks>
public sealed class PaymentService(
    SiteDbContext db,
    IPayableItemCatalogue catalogue,
    IPaymentGateway gateway,
    IGuestLookupLimiter guestLookupLimiter,
    IOrderReferenceGenerator referenceGenerator,
    IReceiptEmailComposer receiptComposer,
    IOptions<PaymentOptions> options,
    TimeProvider clock,
    ILogger<PaymentService> logger) : IPaymentService
{
    private const int MaxReferenceAttempts = 5;

    private TimeSpan PendingHold => TimeSpan.FromMinutes(Math.Max(1, options.Value.PendingOrderMinutes));

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public Task<PaymentSubject?> FindStudentAsync(string studentId, CancellationToken cancellationToken = default) =>
        FindSubjectAsync(PaymentSubjectKind.Student, studentId, cancellationToken);

    public async Task<PaymentSubject?> FindSubjectAsync(PaymentSubjectKind kind, string reference, CancellationToken cancellationToken = default)
    {
        var key = Normalise(reference);
        if (key.Length == 0)
        {
            return null;
        }

        return kind switch
        {
            PaymentSubjectKind.Student => await FindStudentSubjectAsync(key, cancellationToken),
            PaymentSubjectKind.Applicant => await FindApplicantSubjectAsync(key, cancellationToken),
            _ => null
        };
    }

    public async Task<GuestLookupResult> GuestLookupAsync(string studentId, DateOnly dateOfBirth, string clientKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientKey);

        var key = Normalise(studentId);

        // The attempt is counted before the lookup, in one atomic step per client and per student ID, so parallel
        // guesses can't all pass a check made before any of them is recorded. A correct answer gives it back.
        var attempt = guestLookupLimiter.TryBegin(clientKey, key);
        if (!attempt.Allowed)
        {
            logger.LogWarning("Guest payment lookup blocked for client {ClientKey}: too many failed attempts", clientKey);
            return new GuestLookupResult(GuestLookupOutcome.TooManyAttempts, null, 0);
        }

        var student = key.Length == 0
            ? null
            : await db.SyncedStudents.AsNoTracking().FirstOrDefaultAsync(s => s.StudentId == key, cancellationToken);

        if (student is null || student.DateOfBirth != dateOfBirth)
        {
            logger.LogInformation("Guest payment lookup failed for client {ClientKey}; {AttemptsLeft} attempts left", clientKey, attempt.AttemptsLeft);
            return attempt.AttemptsLeft <= 0
                ? new GuestLookupResult(GuestLookupOutcome.TooManyAttempts, null, 0)
                : new GuestLookupResult(GuestLookupOutcome.NotFound, null, attempt.AttemptsLeft);
        }

        guestLookupLimiter.Release(attempt);
        var subject = await FindStudentSubjectAsync(student.StudentId, cancellationToken);
        return subject is null
            ? new GuestLookupResult(GuestLookupOutcome.NotFound, null, guestLookupLimiter.AttemptsLeft(clientKey))
            : new GuestLookupResult(GuestLookupOutcome.Found, subject, guestLookupLimiter.AttemptsLeft(clientKey));
    }

    public async Task<IReadOnlyList<DueItem>> GetDueItemsAsync(PaymentSubject subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var items = (await catalogue.GetAllAsync(cancellationToken)).Where(item => IsDue(item, subject)).ToList();
        if (items.Count == 0)
        {
            return [];
        }

        var itemKeys = items.Select(i => i.Key).ToList();
        var paidKeys = (await db.PaymentOrders.AsNoTracking()
                .Where(o => o.SubjectReference == subject.Reference && o.Status == OrderStatus.Paid && itemKeys.Contains(o.ItemKey))
                .Select(o => o.ItemKey)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var ukNow = UkTime.NowAt(clock);
        var due = new List<DueItem>(items.Count);
        foreach (var item in items)
        {
            var placesLeft = item.Places is { } places
                ? places - await PlacesTakenAsync(item.Key, subject.Reference, cancellationToken)
                : (int?)null;
            due.Add(new DueItem(item, placesLeft is null ? null : Math.Max(0, placesLeft.Value), paidKeys.Contains(item.Key))
            {
                Closed = IsClosed(item, ukNow)
            });
        }

        return due
            .OrderBy(d => d.Item.DueDate ?? DateTime.MaxValue)
            .ThenBy(d => d.Item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<StartOrderResult> StartOrderAsync(
        PaymentSubject subject,
        Guid itemKey,
        Payer payer,
        Guid? paymentLinkId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(payer);

        var item = await catalogue.GetAsync(itemKey, cancellationToken);
        if (item is null || !IsDue(item, subject))
        {
            return new StartOrderResult(StartOrderOutcome.NotAvailable, null);
        }

        for (var attempt = 1; ; attempt++)
        {
            var staleSessions = new List<string>();
            try
            {
                var result = await StartOrderInTransactionAsync(subject, item, payer, paymentLinkId, staleSessions, cancellationToken);

                // Only once the expiry is committed: close the provider's side so the abandoned checkout can't be paid later.
                await ExpireSessionsAsync(staleSessions, cancellationToken);
                return result;
            }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex))
            {
                db.ChangeTracker.Clear();

                // Another request created the live order for this subject and item first: use that one.
                var existing = await ActiveOrderAsync(subject.Reference, item.Key, cancellationToken);
                if (existing is not null)
                {
                    logger.LogInformation("Concurrent start for {Item} / {Subject}; reusing order {Reference}", item.Key, subject.Reference, existing.Reference);
                    return existing.Status == OrderStatus.Paid
                        ? new StartOrderResult(StartOrderOutcome.AlreadyPaid, existing)
                        : new StartOrderResult(StartOrderOutcome.Started, existing);
                }

                // Otherwise the clash was on the order reference itself; try again with a new one.
                if (attempt >= MaxReferenceAttempts)
                {
                    throw;
                }
            }
        }
    }

    public async Task<PaymentOrder?> GetOrderAsync(string reference, CancellationToken cancellationToken = default)
    {
        var key = Normalise(reference);
        return key.Length == 0
            ? null
            : await db.PaymentOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Reference == key, cancellationToken);
    }

    public async Task<GatewayCheckout> BeginCheckoutAsync(string orderReference, CheckoutUrls urls, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(urls);

        var key = Normalise(orderReference);
        var order = await db.PaymentOrders.FirstOrDefaultAsync(o => o.Reference == key, cancellationToken)
                    ?? throw new InvalidOperationException($"Order {orderReference} was not found.");
        if (order.Status != OrderStatus.Pending)
        {
            throw new InvalidOperationException($"Order {order.Reference} is {order.Status} and cannot be checked out.");
        }

        if (!string.IsNullOrEmpty(order.GatewaySessionId))
        {
            // Checking out again (back button, second tab). If the earlier session was actually paid, never open another one.
            var earlier = await gateway.GetStatusAsync(order.GatewaySessionId, cancellationToken);
            if (earlier.State == GatewayPaymentState.Paid)
            {
                await MarkPaidAsync(order.Id, earlier.PaymentId, cancellationToken);
                throw new InvalidOperationException($"Order {order.Reference} has already been paid and cannot be checked out again.");
            }
        }

        var checkout = await gateway.CreateCheckoutAsync(order, urls, cancellationToken);
        order.GatewaySessionId = checkout.SessionId;
        order.Gateway = gateway.Name;
        order.UpdatedUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Checkout session {SessionId} created for order {Reference} via {Gateway}", checkout.SessionId, order.Reference, gateway.Name);
        return checkout;
    }

    public async Task<PaymentOrder?> ConfirmReturnAsync(string orderReference, CancellationToken cancellationToken = default)
    {
        var order = await GetOrderAsync(orderReference, cancellationToken);
        if (order is null || order.Status == OrderStatus.Paid || string.IsNullOrEmpty(order.GatewaySessionId))
        {
            return order;
        }

        var status = await gateway.GetStatusAsync(order.GatewaySessionId, cancellationToken);
        return status.State == GatewayPaymentState.Paid
            ? await MarkPaidAsync(order.Id, status.PaymentId, cancellationToken)
            : order;
    }

    public async Task<PaymentOrder?> MarkPaidAsync(Guid orderId, string? gatewayPaymentId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var order = await db.PaymentOrders.FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
            if (order is null)
            {
                logger.LogWarning("Asked to mark unknown order {OrderId} as paid", orderId);
                return null;
            }

            if (order.Status is OrderStatus.Paid or OrderStatus.PaidDuplicate or OrderStatus.Refunded)
            {
                // Already recorded (or refunded since): a repeat confirmation changes nothing.
                return order;
            }

            var now = UtcNow;
            var wasPending = order.Status == OrderStatus.Pending;
            var cancelledSessions = new List<string>();

            // The provider took the money after the order expired or was cancelled (or, in a race, alongside another
            // order). Money has moved, so record it either way. It is a normal payment unless this subject has already
            // paid for this item on another order, in which case it is a duplicate that finance must refund.
            // The active marker stays as it is: a newer order for the same item may hold it now.
            var alreadyPaidElsewhere = await db.PaymentOrders.AsNoTracking().AnyAsync(
                o => o.Id != order.Id
                     && o.SubjectReference == order.SubjectReference
                     && o.ItemKey == order.ItemKey
                     && o.Status == OrderStatus.Paid,
                cancellationToken);

            order.PaidUtc = now;
            order.UpdatedUtc = now;
            order.GatewayPaymentId = gatewayPaymentId ?? order.GatewayPaymentId;

            if (alreadyPaidElsewhere)
            {
                logger.LogWarning(
                    "Order {Reference} was {Status} when payment {PaymentId} arrived and the item is already paid on another order; recording a duplicate to refund",
                    order.Reference,
                    order.Status,
                    gatewayPaymentId);
                order.Status = OrderStatus.PaidDuplicate;
                order.ActiveMarker = null;
                db.OutboxEmails.Add(receiptComposer.ComposeDuplicatePaymentNotice(order, FinanceEmail, now));
            }
            else
            {
                if (!wasPending)
                {
                    logger.LogWarning("Order {Reference} was {Status} when payment arrived; recording the payment", order.Reference, order.Status);

                    // A newer live order for the same item is now pointless: close it so the payer can't pay a second time.
                    var superseded = await db.PaymentOrders
                        .Where(o => o.Id != order.Id
                                    && o.SubjectReference == order.SubjectReference
                                    && o.ItemKey == order.ItemKey
                                    && o.Status == OrderStatus.Pending)
                        .ToListAsync(cancellationToken);
                    foreach (var other in superseded)
                    {
                        other.Status = OrderStatus.Cancelled;
                        other.ActiveMarker = null;
                        other.UpdatedUtc = now;
                        if (!string.IsNullOrEmpty(other.GatewaySessionId))
                        {
                            cancelledSessions.Add(other.GatewaySessionId);
                        }
                    }
                }

                order.Status = OrderStatus.Paid;

                if (order.PaymentLinkId is { } linkId
                    && await db.PaymentLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken) is { UsedUtc: null } link)
                {
                    link.UsedUtc = now;
                }

                var subjectEmail = await SubjectEmailAsync(order.SubjectKind, order.SubjectReference, cancellationToken);
                db.OutboxEmails.Add(receiptComposer.Compose(order, subjectEmail, now));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation("Order {Reference} {Status} ({PaymentId}); email queued", order.Reference, order.Status, gatewayPaymentId);
                await ExpireSessionsAsync(cancelledSessions, cancellationToken);
                return order;
            }
            catch (DbUpdateException ex) when (DbExceptions.IsUniqueViolation(ex) && attempt < 3)
            {
                // A concurrent confirmation (return page and webhook together) saved first. Reload and
                // the next pass sees the order already paid.
                db.ChangeTracker.Clear();
            }
        }
    }

    public async Task<IReadOnlyList<PaymentOrder>> RecentOrdersAsync(int take, CancellationToken cancellationToken = default) =>
        await db.PaymentOrders.AsNoTracking()
            .OrderByDescending(o => o.CreatedUtc)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

    private async Task<StartOrderResult> StartOrderInTransactionAsync(
        PaymentSubject subject,
        PayableItem item,
        Payer payer,
        Guid? paymentLinkId,
        List<string> staleSessions,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = UtcNow;

        var live = await db.PaymentOrders
            .Where(o => o.SubjectReference == subject.Reference && o.ItemKey == item.Key)
            .Where(o => o.ActiveMarker != null || o.Status == OrderStatus.Paid)
            .ToListAsync(cancellationToken);

        if (live.FirstOrDefault(o => o.Status == OrderStatus.Paid) is { } paid)
        {
            return new StartOrderResult(StartOrderOutcome.AlreadyPaid, paid);
        }

        if (live.FirstOrDefault(o => o.Status == OrderStatus.Pending && o.ActiveMarker != null) is { } pending)
        {
            if (now - pending.CreatedUtc < PendingHold)
            {
                // A refresh or second click returns the same order rather than creating another.
                ApplyPayer(pending, payer);
                pending.PaymentLinkId = paymentLinkId ?? pending.PaymentLinkId;
                pending.UpdatedUtc = now;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new StartOrderResult(StartOrderOutcome.Started, pending);
            }

            pending.Status = OrderStatus.Expired;
            pending.ActiveMarker = null;
            pending.UpdatedUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            if (!string.IsNullOrEmpty(pending.GatewaySessionId))
            {
                staleSessions.Add(pending.GatewaySessionId);
            }
        }

        if (IsClosed(item, UkTime.NowAt(clock)))
        {
            // The due date or the day it takes place has passed. (A payer who started before then keeps their live order, above.)
            await transaction.CommitAsync(cancellationToken);
            return new StartOrderResult(StartOrderOutcome.NotAvailable, null);
        }

        if (item.Places is { } places && places - await PlacesTakenAsync(item.Key, subject.Reference, cancellationToken) <= 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new StartOrderResult(StartOrderOutcome.SoldOut, null);
        }

        var order = new PaymentOrder
        {
            Id = Guid.NewGuid(),
            Reference = referenceGenerator.Next(),
            SubjectKind = subject.Kind,
            SubjectReference = subject.Reference,
            SubjectFirstName = subject.FirstName,
            CourseId = subject.CourseId,
            CourseTitle = subject.CourseTitle,
            CourseCode = subject.CourseCode,
            ItemKey = item.Key,
            ItemName = item.Name,
            ItemType = item.Type,
            CostCode = item.CostCode,
            AmountPence = item.AmountPence,
            Status = OrderStatus.Pending,
            Gateway = gateway.Name,
            PaymentLinkId = paymentLinkId,
            CreatedUtc = now,
            UpdatedUtc = now,
            ActiveMarker = SiteDbContext.ActiveOrderMarker
        };
        ApplyPayer(order, payer);

        db.PaymentOrders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Order {Reference} started for {SubjectKind} {Subject}, item {Item} ({Amount}p)",
            order.Reference,
            subject.Kind,
            subject.Reference,
            item.Key,
            item.AmountPence);
        return new StartOrderResult(StartOrderOutcome.Started, order);
    }

    /// <summary>Paid orders plus recent pending ones (each holds a place), excluding this subject's own pending order.</summary>
    private async Task<int> PlacesTakenAsync(Guid itemKey, string subjectReference, CancellationToken cancellationToken)
    {
        var holdFrom = UtcNow - PendingHold;
        return await db.PaymentOrders.AsNoTracking()
            .Where(o => o.ItemKey == itemKey)
            .CountAsync(
                o => o.Status == OrderStatus.Paid
                     || (o.Status == OrderStatus.Pending && o.CreatedUtc > holdFrom && o.SubjectReference != subjectReference),
                cancellationToken);
    }

    private Task<PaymentOrder?> ActiveOrderAsync(string subjectReference, Guid itemKey, CancellationToken cancellationToken) =>
        db.PaymentOrders.AsNoTracking()
            .Where(o => o.SubjectReference == subjectReference && o.ItemKey == itemKey)
            .Where(o => o.ActiveMarker != null || o.Status == OrderStatus.Paid)
            .OrderByDescending(o => o.Status == OrderStatus.Paid)
            .FirstOrDefaultAsync(cancellationToken);

    private string FinanceEmail =>
        string.IsNullOrWhiteSpace(options.Value.FinanceEmail) ? PaymentOptions.DefaultFinanceEmail : options.Value.FinanceEmail.Trim();

    /// <summary>Closes provider sessions for orders that are no longer live. Best effort: the gateway logs its own failures.</summary>
    private async Task ExpireSessionsAsync(IEnumerable<string> sessionIds, CancellationToken cancellationToken)
    {
        foreach (var sessionId in sessionIds.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await gateway.ExpireAsync(sessionId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not expire checkout session {SessionId}", sessionId);
            }
        }
    }

    /// <summary>
    /// True once the item can no longer be paid for, in UK time: after the end of its due date, or once it takes place
    /// (a date with no time of day counts the whole day, so it closes at the end of that day).
    /// </summary>
    internal static bool IsClosed(PayableItem item, DateTime ukNow)
    {
        if (item.DueDate is { } due && ukNow >= UkTime.AsLocal(due).Date.AddDays(1))
        {
            return true;
        }

        if (item.TakesPlaceOn is { } takesPlace)
        {
            var when = UkTime.AsLocal(takesPlace);
            var closesAt = when.TimeOfDay == TimeSpan.Zero ? when.Date.AddDays(1) : when;
            if (ukNow >= closesAt)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDue(PayableItem item, PaymentSubject subject) =>
        item.AppliesTo(subject.Kind) && item.CourseIds.Contains(subject.CourseId);

    private static void ApplyPayer(PaymentOrder order, Payer payer)
    {
        order.PayerType = payer.Type;
        order.PayerName = payer.Name.Trim();
        order.PayerEmail = payer.Email.Trim();
        order.PayerRelationship = string.IsNullOrWhiteSpace(payer.Relationship) ? null : payer.Relationship.Trim();
    }

    private async Task<PaymentSubject?> FindStudentSubjectAsync(string studentId, CancellationToken cancellationToken)
    {
        var student = await db.SyncedStudents.AsNoTracking().FirstOrDefaultAsync(s => s.StudentId == studentId, cancellationToken);
        if (student is null)
        {
            return null;
        }

        var course = await CourseAsync(student.CourseId, cancellationToken);
        return new PaymentSubject(PaymentSubjectKind.Student, student.StudentId, student.FirstName, student.Email, student.CourseId, course.Title, course.Code);
    }

    private async Task<PaymentSubject?> FindApplicantSubjectAsync(string reference, CancellationToken cancellationToken)
    {
        var application = await db.SyncedApplications.AsNoTracking().FirstOrDefaultAsync(a => a.Reference == reference, cancellationToken);
        if (application is null)
        {
            return null;
        }

        var course = await CourseAsync(application.CourseId, cancellationToken);
        return new PaymentSubject(PaymentSubjectKind.Applicant, application.Reference, application.FirstName, application.Email, application.CourseId, course.Title, course.Code);
    }

    private async Task<(string Title, string? Code)> CourseAsync(int courseId, CancellationToken cancellationToken)
    {
        var course = await db.SyncedCourses.AsNoTracking()
            .Where(c => c.Id == courseId)
            .Select(c => new { c.Heading, c.CourseCode })
            .FirstOrDefaultAsync(cancellationToken);
        return course is null ? ("Your course", null) : (course.Heading, course.CourseCode);
    }

    private async Task<string?> SubjectEmailAsync(PaymentSubjectKind kind, string reference, CancellationToken cancellationToken) =>
        kind == PaymentSubjectKind.Student
            ? await db.SyncedStudents.AsNoTracking().Where(s => s.StudentId == reference).Select(s => s.Email).FirstOrDefaultAsync(cancellationToken)
            : await db.SyncedApplications.AsNoTracking().Where(a => a.Reference == reference).Select(a => a.Email).FirstOrDefaultAsync(cancellationToken);

    /// <summary>References are matched case-insensitively and ignoring surrounding spaces ("app-2027-00412 " works).</summary>
    private static string Normalise(string? reference) => (reference ?? string.Empty).Trim().ToUpperInvariant();
}
