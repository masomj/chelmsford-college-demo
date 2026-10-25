using Chelmsford.Core.Courses;
using Chelmsford.Core.Integration;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Data;
using Chelmsford.Infrastructure.Payments;
using Chelmsford.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chelmsford.Tests;

public sealed partial class PaymentServiceTests : IDisposable
{
    private const string StudentId = "21004567";
    private const string ApplicantRef = "APP-2027-00412";
    private static readonly DateOnly StudentDob = new(2009, 5, 14);

    private static readonly PayableItem MaterialsFee = new(
        Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a1001"), "Fashion materials fee", PayableItemType.MaterialsFee,
        null, 4500, [374], null, null, "MAT-FASH", null);

    private static readonly PayableItem Trip = new(
        Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a1002"), "London Fashion Week trip", PayableItemType.Trip,
        null, 3500, [374, 540], null, 1, "TRIP-LFW", new DateTime(2027, 2, 14, 0, 0, 0, DateTimeKind.Utc));

    private static readonly PayableItem Deposit = new(
        Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a1003"), "Course deposit", PayableItemType.CourseDeposit,
        null, 5000, [482], null, null, "DEP", null);

    // Course 999 items, for the closing-date tests. The test clock starts at 09:00 UTC on 1 October 2026 (10:00 BST).
    private const string MiaId = "21005000";

    private static readonly PayableItem OverdueFee = new(
        Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a2001"), "Overdue fee", PayableItemType.MaterialsFee,
        null, 1000, [999], new DateTime(2026, 9, 30), null, "OVER", null);

    private static readonly PayableItem DueTodayFee = new(
        Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a2002"), "Due today fee", PayableItemType.MaterialsFee,
        null, 1200, [999], new DateTime(2026, 10, 1), null, "TODAY", null);

    private static readonly PayableItem PastTrip = new(
        Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a2003"), "Last week's trip", PayableItemType.Trip,
        null, 1500, [999], null, null, "PAST", new DateTime(2026, 9, 24));

    private static readonly Payer Parent = new(PayerType.ParentOrCarer, "Sarah Turner", "sarah@example.com", "Mother");

    private readonly TestDatabase _database = new();
    private readonly TestClock _clock = new();
    private readonly FakeGateway _gateway = new();
    private readonly FakeCatalogue _catalogue = new(MaterialsFee, Trip, Deposit, OverdueFee, DueTodayFee, PastTrip);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IOptions<PaymentOptions> _options = Options.Create(new PaymentOptions());

    public PaymentServiceTests()
    {
        using var db = _database.CreateContext();
        db.SyncedCourses.AddRange(
            TestCourses.Course(374, "Fashion Design", "Creative Design", 4, "Level 3"),
            TestCourses.Course(540, "Carpentry", "Construction", 3, "Level 2"),
            TestCourses.Course(482, "Hairdressing", "Hair and Beauty", 3, "Level 2"),
            TestCourses.Course(999, "Sample Course", "Creative Design", 3, "Level 2"));
        db.SyncedStudents.AddRange(
            new SyncedStudent { StudentId = StudentId, FirstName = "Ellie", LastName = "Turner", DateOfBirth = StudentDob, Email = "ellie.turner@students.example", CourseId = 374 },
            new SyncedStudent { StudentId = "21004890", FirstName = "Josh", LastName = "Patel", DateOfBirth = new DateOnly(2008, 11, 2), Email = "josh.patel@students.example", CourseId = 540 },
            new SyncedStudent { StudentId = MiaId, FirstName = "Mia", LastName = "Okafor", DateOfBirth = new DateOnly(2009, 3, 3), Email = "mia.okafor@students.example", CourseId = 999 });
        db.SyncedApplications.Add(new SyncedApplication { Reference = ApplicantRef, FirstName = "Ruby", LastName = "Hughes", Email = "ruby.hughes@example.com", CourseId = 482 });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _database.Dispose();
    }

    private PaymentService CreateService(SiteDbContext db) => new(
        db,
        _catalogue,
        _gateway,
        new GuestLookupLimiter(_cache, _options, _clock),
        new OrderReferenceGenerator(),
        new ReceiptEmailComposer(),
        _options,
        _clock,
        NullLogger<PaymentService>.Instance);

    private async Task<T> WithServiceAsync<T>(Func<PaymentService, Task<T>> action)
    {
        await using var db = _database.CreateContext();
        return await action(CreateService(db));
    }

    private Task<PaymentSubject> StudentAsync(string id = StudentId) =>
        WithServiceAsync(async s => (await s.FindStudentAsync(id))!);

    [Fact]
    public async Task Finds_students_and_applicants_with_their_course()
    {
        var student = await StudentAsync();
        var applicant = await WithServiceAsync(s => s.FindSubjectAsync(PaymentSubjectKind.Applicant, " app-2027-00412 "));

        Assert.Equal("Ellie", student.FirstName);
        Assert.Equal("Fashion Design", student.CourseTitle);
        Assert.NotNull(applicant);
        Assert.Equal(PaymentSubjectKind.Applicant, applicant!.Kind);
        Assert.Equal(482, applicant.CourseId);
    }

    [Fact]
    public async Task Due_items_match_course_and_subject_kind()
    {
        var student = await StudentAsync();
        var applicant = (await WithServiceAsync(s => s.FindSubjectAsync(PaymentSubjectKind.Applicant, ApplicantRef)))!;

        var studentItems = await WithServiceAsync(s => s.GetDueItemsAsync(student));
        var applicantItems = await WithServiceAsync(s => s.GetDueItemsAsync(applicant));

        Assert.Equal([MaterialsFee.Key, Trip.Key], studentItems.Select(d => d.Item.Key).Order());
        Assert.Equal(1, studentItems.Single(d => d.Item.Key == Trip.Key).PlacesLeft);
        Assert.Null(studentItems.Single(d => d.Item.Key == MaterialsFee.Key).PlacesLeft);
        Assert.Equal([Deposit.Key], applicantItems.Select(d => d.Item.Key));
    }

    [Fact]
    public async Task Starting_twice_returns_the_same_order()
    {
        var student = await StudentAsync();

        var first = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent with { Name = "Sarah J Turner" }, null));

        Assert.Equal(StartOrderOutcome.Started, first.Outcome);
        Assert.Equal(StartOrderOutcome.Started, second.Outcome);
        Assert.Equal(first.Order!.Id, second.Order!.Id);
        Assert.Equal("Sarah J Turner", second.Order.PayerName);
        Assert.Matches("^CC-[A-HJ-NP-Z2-9]{6}$", first.Order.Reference);
        Assert.Equal("Fashion Design", first.Order.CourseTitle);
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.PaymentOrders.CountAsync());
    }

    [Fact]
    public async Task A_stale_pending_order_is_expired_and_replaced()
    {
        var student = await StudentAsync();

        var first = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));
        _clock.Advance(TimeSpan.FromMinutes(31));
        var second = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));

        Assert.NotEqual(first.Order!.Id, second.Order!.Id);
        await using var db = _database.CreateContext();
        var old = await db.PaymentOrders.SingleAsync(o => o.Id == first.Order.Id);
        Assert.Equal(OrderStatus.Expired, old.Status);
        Assert.Null(old.ActiveMarker);
    }

    [Fact]
    public async Task After_payment_starting_again_returns_already_paid()
    {
        var student = await StudentAsync();
        var started = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));
        await WithServiceAsync(s => s.MarkPaidAsync(started.Order!.Id, "pi_123"));

        var again = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));
        var due = await WithServiceAsync(s => s.GetDueItemsAsync(student));

        Assert.Equal(StartOrderOutcome.AlreadyPaid, again.Outcome);
        Assert.Equal(started.Order!.Id, again.Order!.Id);
        Assert.True(due.Single(d => d.Item.Key == MaterialsFee.Key).AlreadyPaid);
    }

    [Fact]
    public async Task Marking_paid_twice_queues_exactly_one_receipt()
    {
        var student = await StudentAsync();
        var started = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));

        var first = await WithServiceAsync(s => s.MarkPaidAsync(started.Order!.Id, "pi_123"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await WithServiceAsync(s => s.MarkPaidAsync(started.Order!.Id, "pi_456"));

        Assert.Equal(OrderStatus.Paid, first!.Status);
        Assert.Equal("pi_123", second!.GatewayPaymentId);
        Assert.Equal(first.PaidUtc, second.PaidUtc);

        await using var db = _database.CreateContext();
        var receipt = Assert.Single(await db.OutboxEmails.ToListAsync());
        Assert.Equal($"receipt:{started.Order!.Id:D}", receipt.CorrelationKey);
        Assert.Equal("sarah@example.com", receipt.To);
        Assert.Equal("ellie.turner@students.example", receipt.Cc);
        Assert.Contains(started.Order.Reference, receipt.Body, StringComparison.Ordinal);
        Assert.Contains("£45.00", receipt.Body, StringComparison.Ordinal);
        Assert.Contains("Ellie", receipt.Body, StringComparison.Ordinal);
        Assert.Contains("Fashion Design", receipt.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trip_capacity_is_enforced()
    {
        var ellie = await StudentAsync();
        var josh = await StudentAsync("21004890");

        var ellieOrder = await WithServiceAsync(s => s.StartOrderAsync(ellie, Trip.Key, Parent, null));
        var joshOrder = await WithServiceAsync(s => s.StartOrderAsync(josh, Trip.Key, new Payer(PayerType.Student, "Josh Patel", "josh.patel@students.example", null), null));
        var joshDue = await WithServiceAsync(s => s.GetDueItemsAsync(josh));
        var ellieDue = await WithServiceAsync(s => s.GetDueItemsAsync(ellie));

        Assert.Equal(StartOrderOutcome.Started, ellieOrder.Outcome);
        Assert.Equal(StartOrderOutcome.SoldOut, joshOrder.Outcome);
        Assert.True(joshDue.Single().SoldOut);
        Assert.Equal(1, ellieDue.Single(d => d.Item.Key == Trip.Key).PlacesLeft);
    }

    [Fact]
    public async Task An_abandoned_pending_order_releases_its_trip_place()
    {
        var ellie = await StudentAsync();
        var josh = await StudentAsync("21004890");

        await WithServiceAsync(s => s.StartOrderAsync(ellie, Trip.Key, Parent, null));
        _clock.Advance(TimeSpan.FromMinutes(31));
        var joshOrder = await WithServiceAsync(s => s.StartOrderAsync(josh, Trip.Key, new Payer(PayerType.Student, "Josh Patel", "josh.patel@students.example", null), null));

        Assert.Equal(StartOrderOutcome.Started, joshOrder.Outcome);
    }

    [Fact]
    public async Task Items_not_due_for_the_subject_are_not_available()
    {
        var student = await StudentAsync();

        var deposit = await WithServiceAsync(s => s.StartOrderAsync(student, Deposit.Key, Parent, null));
        var unknown = await WithServiceAsync(s => s.StartOrderAsync(student, Guid.NewGuid(), Parent, null));

        Assert.Equal(StartOrderOutcome.NotAvailable, deposit.Outcome);
        Assert.Equal(StartOrderOutcome.NotAvailable, unknown.Outcome);
    }

    [Fact]
    public async Task Checkout_and_return_record_the_payment()
    {
        var student = await StudentAsync();
        var started = await WithServiceAsync(s => s.StartOrderAsync(student, MaterialsFee.Key, Parent, null));

        var checkout = await WithServiceAsync(s => s.BeginCheckoutAsync(started.Order!.Reference, new CheckoutUrls("https://site/ok", "https://site/cancel")));
        var stillPending = await WithServiceAsync(s => s.ConfirmReturnAsync(started.Order!.Reference));
        _gateway.NextState = GatewayPaymentState.Paid;
        var paid = await WithServiceAsync(s => s.ConfirmReturnAsync(started.Order!.Reference));

        Assert.Equal("sess_" + started.Order!.Reference, checkout.SessionId);
        Assert.Equal(OrderStatus.Pending, stillPending!.Status);
        Assert.Equal(OrderStatus.Paid, paid!.Status);
        Assert.Equal("pi_" + checkout.SessionId, paid.GatewayPaymentId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithServiceAsync(s => s.BeginCheckoutAsync(started.Order.Reference, new CheckoutUrls("a", "b"))));
    }

    [Fact]
    public async Task Guest_lookup_needs_the_exact_date_of_birth()
    {
        var found = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob, "client-a"));
        var wrongDob = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob.AddDays(1), "client-a"));

        Assert.Equal(GuestLookupOutcome.Found, found.Outcome);
        Assert.Equal("Ellie", found.Subject!.FirstName);
        Assert.Equal(GuestLookupOutcome.NotFound, wrongDob.Outcome);
        Assert.Null(wrongDob.Subject);
        Assert.Equal(4, wrongDob.AttemptsLeft);
    }

    [Fact]
    public async Task Guest_lookup_locks_after_five_failures_for_fifteen_minutes()
    {
        for (var i = 0; i < 4; i++)
        {
            var miss = await WithServiceAsync(s => s.GuestLookupAsync("99999999", StudentDob, "client-b"));
            Assert.Equal(GuestLookupOutcome.NotFound, miss.Outcome);
        }

        var fifth = await WithServiceAsync(s => s.GuestLookupAsync("99999999", StudentDob, "client-b"));
        var correctButLocked = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob, "client-b"));
        var otherClient = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob, "client-c"));
        _clock.Advance(TimeSpan.FromMinutes(16));
        var afterWindow = await WithServiceAsync(s => s.GuestLookupAsync(StudentId, StudentDob, "client-b"));

        Assert.Equal(GuestLookupOutcome.TooManyAttempts, fifth.Outcome);
        Assert.Equal(GuestLookupOutcome.TooManyAttempts, correctButLocked.Outcome);
        Assert.Equal(0, correctButLocked.AttemptsLeft);
        Assert.Equal(GuestLookupOutcome.Found, otherClient.Outcome);
        Assert.Equal(GuestLookupOutcome.Found, afterWindow.Outcome);
    }
}
