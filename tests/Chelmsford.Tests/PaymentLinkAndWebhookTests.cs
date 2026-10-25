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

public sealed class PaymentLinkAndWebhookTests : IDisposable
{
    private const string StudentId = "21004567";

    private static readonly PayableItem MaterialsFee = new(
        Guid.Parse("7a1c1d4e-0000-4000-8000-000000000001"), "Fashion materials fee", PayableItemType.MaterialsFee,
        null, 4500, [374], null, null, "MAT-FASH", null);

    private readonly TestDatabase _database = new();
    private readonly TestClock _clock = new();
    private readonly FakeCatalogue _catalogue = new(MaterialsFee);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly IOptions<PaymentOptions> _options = Options.Create(new PaymentOptions());

    public PaymentLinkAndWebhookTests()
    {
        using var db = _database.CreateContext();
        db.SyncedCourses.Add(TestCourses.Course(374, "Fashion Design", "Creative Design", 4, "Level 3"));
        db.SyncedStudents.Add(new SyncedStudent
        {
            StudentId = StudentId, FirstName = "Ellie", LastName = "Turner", DateOfBirth = new DateOnly(2009, 5, 14),
            Email = "ellie.turner@students.example", CourseId = 374
        });
        db.SaveChanges();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _database.Dispose();
    }

    private PaymentService Payments(SiteDbContext db) => new(
        db, _catalogue, new FakeGateway(), new GuestLookupLimiter(_cache, _options, _clock), new OrderReferenceGenerator(),
        new ReceiptEmailComposer(), _options, _clock, NullLogger<PaymentService>.Instance);

    private PaymentLinkService Links(SiteDbContext db) =>
        new(db, Payments(db), _catalogue, _clock, NullLogger<PaymentLinkService>.Instance);

    private StripeWebhookHandler Webhooks(SiteDbContext db, string? secret = "whsec_test") =>
        new(db, Payments(db), Options.Create(new StripeOptions { WebhookSecret = secret }), _clock, NullLogger<StripeWebhookHandler>.Instance);

    private async Task<string> CreateLinkAsync(TimeSpan validFor)
    {
        await using var db = _database.CreateContext();
        return await Links(db).CreateAsync(PaymentSubjectKind.Student, StudentId, MaterialsFee.Key, validFor);
    }

    private async Task<ResolvedPaymentLink?> ResolveAsync(string token)
    {
        await using var db = _database.CreateContext();
        return await Links(db).ResolveAsync(token);
    }

    private async Task<PaymentOrder> StartOrderAsync(Guid? linkId = null)
    {
        await using var db = _database.CreateContext();
        var payments = Payments(db);
        var subject = (await payments.FindStudentAsync(StudentId))!;
        var result = await payments.StartOrderAsync(subject, MaterialsFee.Key, new Payer(PayerType.Student, "Ellie Turner", "ellie.turner@students.example", null), linkId);
        return result.Order!;
    }

    [Fact]
    public async Task Only_the_token_hash_is_stored()
    {
        var token = await CreateLinkAsync(TimeSpan.FromDays(7));

        await using var db = _database.CreateContext();
        var link = await db.PaymentLinks.SingleAsync();
        Assert.True(token.Length >= 43);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.NotEqual(token, link.TokenHash);
        Assert.Equal(PaymentLinkService.Hash(token), link.TokenHash);
    }

    [Fact]
    public async Task A_payment_link_resolves_until_it_is_used()
    {
        var token = await CreateLinkAsync(TimeSpan.FromDays(7));

        var resolved = await ResolveAsync(token);
        Assert.NotNull(resolved);
        Assert.Equal("Ellie", resolved!.Subject.FirstName);
        Assert.Equal(MaterialsFee.Key, resolved.Item.Key);

        var order = await StartOrderAsync(resolved.Link.Id);
        await using (var db = _database.CreateContext())
        {
            await Payments(db).MarkPaidAsync(order.Id, "pi_1");
        }

        Assert.Null(await ResolveAsync(token));
        await using var check = _database.CreateContext();
        Assert.NotNull((await check.PaymentLinks.SingleAsync()).UsedUtc);
    }

    [Fact]
    public async Task A_payment_link_expires()
    {
        var token = await CreateLinkAsync(TimeSpan.FromHours(1));

        _clock.Advance(TimeSpan.FromMinutes(59));
        Assert.NotNull(await ResolveAsync(token));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await ResolveAsync(token));
    }

    [Fact]
    public async Task Unknown_tokens_do_not_resolve()
    {
        await CreateLinkAsync(TimeSpan.FromDays(1));

        Assert.Null(await ResolveAsync("not-a-real-token"));
        Assert.Null(await ResolveAsync(""));
    }

    [Fact]
    public async Task Webhook_without_a_configured_secret_is_rejected()
    {
        await using var db = _database.CreateContext();

        Assert.Equal(WebhookOutcome.InvalidSignature, await Webhooks(db, secret: null).HandleAsync("{}", "t=1,v1=abc"));
    }

    [Fact]
    public async Task Webhook_with_a_bad_signature_is_rejected()
    {
        await using var db = _database.CreateContext();

        Assert.Equal(WebhookOutcome.InvalidSignature, await Webhooks(db).HandleAsync("{\"id\":\"evt_1\"}", "t=1,v1=abc"));
        Assert.Empty(await db.ProcessedWebhookEvents.ToListAsync());
    }

    [Fact]
    public async Task Webhook_marks_the_order_paid_once_and_flags_a_repeat_as_duplicate()
    {
        var order = await StartOrderAsync();
        var notification = new WebhookNotification("evt_123", StripeWebhookHandler.CheckoutSessionCompleted, "paid", order.Id.ToString("D"), "pi_999");

        WebhookOutcome first;
        await using (var db = _database.CreateContext())
        {
            first = await Webhooks(db).ProcessAsync(notification, CancellationToken.None);
        }

        WebhookOutcome repeat;
        await using (var db = _database.CreateContext())
        {
            repeat = await Webhooks(db).ProcessAsync(notification, CancellationToken.None);
        }

        Assert.Equal(WebhookOutcome.Processed, first);
        Assert.Equal(WebhookOutcome.Duplicate, repeat);
        await using var check = _database.CreateContext();
        var paid = await check.PaymentOrders.SingleAsync();
        Assert.Equal(OrderStatus.Paid, paid.Status);
        Assert.Equal("pi_999", paid.GatewayPaymentId);
        Assert.Single(await check.OutboxEmails.ToListAsync());
        Assert.Single(await check.ProcessedWebhookEvents.ToListAsync());
    }

    [Fact]
    public async Task Other_webhook_events_are_ignored()
    {
        var order = await StartOrderAsync();

        await using var db = _database.CreateContext();
        var handler = Webhooks(db);
        var other = await handler.ProcessAsync(new WebhookNotification("evt_a", "payment_intent.created", null, null, null), CancellationToken.None);
        var unpaid = await handler.ProcessAsync(new WebhookNotification("evt_b", StripeWebhookHandler.CheckoutSessionCompleted, "unpaid", order.Id.ToString("D"), null), CancellationToken.None);

        Assert.Equal(WebhookOutcome.Ignored, other);
        Assert.Equal(WebhookOutcome.Ignored, unpaid);
        Assert.Equal(OrderStatus.Pending, (await db.PaymentOrders.AsNoTracking().SingleAsync()).Status);
    }
}
