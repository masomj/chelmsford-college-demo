using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chelmsford.Tests;

/// <summary>Exactly-once safeguards: session expiry, late and duplicate payments, closing dates and lookup limits.</summary>
public sealed partial class PaymentServiceTests
{
    private static readonly CheckoutUrls Urls = new("https://site/ok", "https://site/cancel");

    private async Task<PaymentOrder> StartAndCheckoutAsync(PaymentSubject subject, PayableItem item)
    {
        var started = await WithServiceAsync(s => s.StartOrderAsync(subject, item.Key, Parent, null));
        await WithServiceAsync(s => s.BeginCheckoutAsync(started.Order!.Reference, Urls));
        return started.Order!;
    }

    [Fact]
    public async Task Replacing_a_stale_pending_order_also_expires_its_checkout_session()
    {
        var student = await StudentAsync();
        var first = await StartAndCheckoutAsync(student, MaterialsFee);

        _clock.Advance(TimeSpan.FromMinutes(31));
        await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));

        Assert.Equal(["sess_" + first.Reference], _gateway.ExpiredSessions);
    }

    [Fact]
    public async Task A_live_pending_order_keeps_its_session()
    {
        var student = await StudentAsync();
        await StartAndCheckoutAsync(student, MaterialsFee);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));

        Assert.Empty(_gateway.ExpiredSessions);
    }

    [Fact]
    public async Task A_late_payment_on_an_expired_order_is_recorded_as_paid_when_nothing_else_was_paid()
    {
        var student = await StudentAsync();
        var first = await StartAndCheckoutAsync(student, MaterialsFee);
        _clock.Advance(TimeSpan.FromMinutes(31));
        await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));

        var paid = await WithServiceAsync(s => s.MarkPaidAsync(first.Id, "pi_late"));

        Assert.Equal(OrderStatus.Paid, paid!.Status);
        await using var db = _database.CreateContext();
        Assert.Equal(OrderStatus.Paid, (await db.PaymentOrders.SingleAsync(o => o.Id == first.Id)).Status);
        Assert.Single(await db.OutboxEmails.Where(e => e.CorrelationKey == $"receipt:{first.Id:D}").ToListAsync());
        Assert.DoesNotContain(await db.OutboxEmails.ToListAsync(), e => e.CorrelationKey.StartsWith("duplicate:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_late_payment_closes_the_newer_pending_order_and_its_session()
    {
        var student = await StudentAsync();
        var first = await StartAndCheckoutAsync(student, MaterialsFee);
        _clock.Advance(TimeSpan.FromMinutes(31));
        _gateway.ExpiredSessions.Clear();
        var second = await StartAndCheckoutAsync(student, MaterialsFee);

        await WithServiceAsync(s => s.MarkPaidAsync(first.Id, "pi_late"));

        await using var db = _database.CreateContext();
        var newer = await db.PaymentOrders.SingleAsync(o => o.Id == second.Id);
        Assert.Equal(OrderStatus.Cancelled, newer.Status);
        Assert.Null(newer.ActiveMarker);
        Assert.Contains("sess_" + second.Reference, _gateway.ExpiredSessions);
        Assert.Equal(StartOrderOutcome.AlreadyPaid, (await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null))).Outcome);
    }

    [Fact]
    public async Task A_payment_on_an_expired_order_is_a_duplicate_when_the_item_was_paid_on_another_order()
    {
        var student = await StudentAsync();
        var first = await StartOrderAsync(student);
        _clock.Advance(TimeSpan.FromMinutes(31));
        var second = await StartOrderAsync(student);
        await WithServiceAsync(s => s.MarkPaidAsync(second.Id, "pi_second"));

        var late = await WithServiceAsync(s => s.MarkPaidAsync(first.Id, "pi_first"));
        var repeat = await WithServiceAsync(s => s.MarkPaidAsync(first.Id, "pi_first"));

        Assert.Equal(OrderStatus.PaidDuplicate, late!.Status);
        Assert.Equal(OrderStatus.PaidDuplicate, repeat!.Status);
        await using var db = _database.CreateContext();
        Assert.Equal(OrderStatus.Paid, (await db.PaymentOrders.SingleAsync(o => o.Id == second.Id)).Status);
        var duplicate = await db.PaymentOrders.SingleAsync(o => o.Id == first.Id);
        Assert.Equal("pi_first", duplicate.GatewayPaymentId);
        Assert.NotNull(duplicate.PaidUtc);
        Assert.Null(duplicate.ActiveMarker);

        var emails = await db.OutboxEmails.ToListAsync();
        var finance = Assert.Single(emails, e => e.CorrelationKey == $"duplicate:{first.Id:D}");
        Assert.Equal("finance@example.ac.uk", finance.To);
        Assert.Contains(first.Reference, finance.Body, StringComparison.Ordinal);
        Assert.Contains("pi_first", finance.Body, StringComparison.Ordinal);
        Assert.Contains("£45.00", finance.Body, StringComparison.Ordinal);
        Assert.Contains("refund", finance.Subject, StringComparison.OrdinalIgnoreCase);

        // The payer gets one receipt, for the order that counts; the duplicate sends none.
        Assert.Single(emails, e => e.CorrelationKey.StartsWith("receipt:", StringComparison.Ordinal));
        Assert.Equal(2, emails.Count);
    }

    [Fact]
    public async Task Duplicate_payment_notices_go_to_the_configured_finance_address()
    {
        var student = await StudentAsync();
        var first = await StartOrderAsync(student);
        _clock.Advance(TimeSpan.FromMinutes(31));
        var second = await StartOrderAsync(student);
        await WithServiceAsync(s => s.MarkPaidAsync(second.Id, "pi_second"));
        var options = Options.Create(new PaymentOptions { FinanceEmail = "payments@college.example" });

        await using (var db = _database.CreateContext())
        {
            var service = new PaymentService(
                db, _catalogue, _gateway, new GuestLookupLimiter(_cache, _options, _clock), new OrderReferenceGenerator(),
                new ReceiptEmailComposer(), options, _clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentService>.Instance);
            await service.MarkPaidAsync(first.Id, "pi_first");
        }

        await using var check = _database.CreateContext();
        Assert.Equal("payments@college.example", (await check.OutboxEmails.SingleAsync(e => e.CorrelationKey == $"duplicate:{first.Id:D}")).To);
    }

    [Fact]
    public async Task Checking_out_again_after_the_session_was_paid_records_the_payment_and_opens_no_second_session()
    {
        var student = await StudentAsync();
        var order = await StartAndCheckoutAsync(student, MaterialsFee);
        _gateway.NextState = GatewayPaymentState.Paid;

        await Assert.ThrowsAsync<InvalidOperationException>(() => WithServiceAsync(s => s.BeginCheckoutAsync(order.Reference, Urls)));

        Assert.Equal(1, _gateway.CheckoutsCreated);
        await using var db = _database.CreateContext();
        Assert.Equal(OrderStatus.Paid, (await db.PaymentOrders.SingleAsync()).Status);
    }

    [Fact]
    public async Task Items_past_their_due_date_or_event_date_are_closed()
    {
        var mia = await StudentAsync(MiaId);

        var due = await WithServiceAsync(s => s.GetDueItemsAsync(mia));

        Assert.True(due.Single(d => d.Item.Key == OverdueFee.Key).Closed);
        Assert.False(due.Single(d => d.Item.Key == OverdueFee.Key).CanPay);
        Assert.True(due.Single(d => d.Item.Key == PastTrip.Key).Closed);
        Assert.False(due.Single(d => d.Item.Key == DueTodayFee.Key).Closed);
        Assert.True(due.Single(d => d.Item.Key == DueTodayFee.Key).CanPay);
    }

    [Fact]
    public async Task Starting_an_order_for_a_closed_item_is_not_available()
    {
        var mia = await StudentAsync(MiaId);

        var overdue = await WithServiceAsync(s => s.StartOrderAsync(mia, OverdueFee.Key, Parent, null));
        var pastTrip = await WithServiceAsync(s => s.StartOrderAsync(mia, PastTrip.Key, Parent, null));
        var open = await WithServiceAsync(s => s.StartOrderAsync(mia, DueTodayFee.Key, Parent, null));

        Assert.Equal(StartOrderOutcome.NotAvailable, overdue.Outcome);
        Assert.Equal(StartOrderOutcome.NotAvailable, pastTrip.Outcome);
        Assert.Equal(StartOrderOutcome.Started, open.Outcome);
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.PaymentOrders.CountAsync());
    }

    [Fact]
    public async Task A_due_date_lasts_until_the_end_of_that_day_in_UK_time()
    {
        var mia = await StudentAsync(MiaId);

        // 23:30 UTC on 1 October is 00:30 BST on 2 October: the day has ended in the UK, though not yet in UTC.
        _clock.Advance(TimeSpan.FromHours(14.5));
        var due = await WithServiceAsync(s => s.GetDueItemsAsync(mia));
        var start = await WithServiceAsync(s => s.StartOrderAsync(mia, DueTodayFee.Key, Parent, null));

        Assert.True(due.Single(d => d.Item.Key == DueTodayFee.Key).Closed);
        Assert.Equal(StartOrderOutcome.NotAvailable, start.Outcome);
    }

    [Fact]
    public async Task A_due_date_is_still_open_late_in_the_day_in_UK_time()
    {
        var mia = await StudentAsync(MiaId);

        // 22:30 UTC on 1 October is 23:30 BST: still the due date.
        _clock.Advance(TimeSpan.FromHours(13.5));
        var due = await WithServiceAsync(s => s.GetDueItemsAsync(mia));

        Assert.False(due.Single(d => d.Item.Key == DueTodayFee.Key).Closed);
    }

    [Fact]
    public void The_closed_flag_is_optional_on_a_due_item_and_stops_payment()
    {
        var item = new DueItem(MaterialsFee, null, false);
        var closed = item with { Closed = true };

        Assert.False(item.Closed);
        Assert.True(item.CanPay);
        Assert.False(closed.CanPay);
    }

    [Fact]
    public async Task One_student_id_cannot_be_guessed_by_changing_client()
    {
        for (var i = 0; i < 10; i++)
        {
            var miss = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob.AddDays(1 + i), $"client-{i}"));
            Assert.Equal(i == 9 ? GuestLookupOutcome.TooManyAttempts : GuestLookupOutcome.NotFound, miss.Outcome);
        }

        var freshClientCorrectDob = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob, "client-new"));
        var differentStudent = await WithServiceAsync(s => s.GuestLookupAsync("21004890", new DateOnly(2008, 11, 2), "client-new"));
        _clock.Advance(TimeSpan.FromMinutes(16));
        var afterWindow = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob, "client-new"));

        Assert.Equal(GuestLookupOutcome.TooManyAttempts, freshClientCorrectDob.Outcome);
        Assert.Equal(0, freshClientCorrectDob.AttemptsLeft);
        Assert.Equal(GuestLookupOutcome.Found, differentStudent.Outcome);
        Assert.Equal(GuestLookupOutcome.Found, afterWindow.Outcome);
    }

    [Fact]
    public void The_limiter_lets_exactly_the_cap_through_under_parallel_attempts()
    {
        var limiter = new GuestLookupLimiter(_cache, _options, _clock);
        var allowed = 0;

        Parallel.For(0, 200, _ =>
        {
            if (limiter.TryBegin("busy-client", null).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(5, allowed);
        Assert.Equal(0, limiter.AttemptsLeft("busy-client"));
    }

    [Fact]
    public void The_student_cap_holds_under_parallel_attempts_from_many_clients()
    {
        var limiter = new GuestLookupLimiter(_cache, _options, _clock);
        var allowed = 0;

        Parallel.For(0, 200, i =>
        {
            if (limiter.TryBegin($"client-{i}", StudentId).Allowed)
            {
                Interlocked.Increment(ref allowed);
            }
        });

        Assert.Equal(10, allowed);
    }

    [Fact]
    public void A_successful_lookup_gives_its_attempt_back_but_failures_stay_counted()
    {
        var limiter = new GuestLookupLimiter(_cache, _options, _clock);

        for (var i = 0; i < 20; i++)
        {
            var attempt = limiter.TryBegin("client", StudentId);
            Assert.True(attempt.Allowed);
            limiter.Release(attempt);
        }

        var failure = limiter.TryBegin("client", StudentId);

        Assert.True(failure.Allowed);
        Assert.Equal(4, failure.AttemptsLeft);
        Assert.Equal(4, limiter.AttemptsLeft("client"));
    }

    [Fact]
    public void Receipts_show_UK_time_not_UTC()
    {
        var order = new PaymentOrder
        {
            Id = Guid.NewGuid(), Reference = "CC-ABC234", ItemName = "Materials fee", AmountPence = 4500, PayerName = "Sarah Turner",
            PayerEmail = "sarah@example.com", PayerType = PayerType.ParentOrCarer, SubjectFirstName = "Ellie", CourseTitle = "Garment Studies",
            PaidUtc = new DateTime(2026, 10, 24, 12, 15, 0, DateTimeKind.Utc)
        };
        var afterChange = new DateTime(2026, 10, 25, 13, 15, 0, DateTimeKind.Utc);

        var summer = new ReceiptEmailComposer().Compose(order, null, afterChange);
        order.PaidUtc = afterChange;
        var winter = new ReceiptEmailComposer().Compose(order, null, afterChange);

        Assert.Contains("Paid on: 24 October 2026 at 1:15pm", summer.Body, StringComparison.Ordinal);
        Assert.Contains("Paid on: 25 October 2026 at 1:15pm", winter.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("UTC", summer.Body, StringComparison.Ordinal);
    }

    private async Task<PaymentOrder> StartOrderAsync(PaymentSubject subject)
    {
        var started = await WithServiceAsync(s => s.StartOrderAsync(subject, MaterialsFee.Key, Parent, null));
        return started.Order!;
    }
}
