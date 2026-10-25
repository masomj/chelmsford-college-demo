using System.Net;
using System.Text;
using Chelmsford.Core.Payments;
using Chelmsford.Infrastructure.Configuration;
using Chelmsford.Infrastructure.Payments;
using Chelmsford.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;

namespace Chelmsford.Tests;

/// <summary>Stripe is replaced by a stub HTTP handler, so these run offline but exercise the real Stripe client.</summary>
public sealed class StripePaymentGatewayTests
{
    private static readonly CheckoutUrls Urls = new("https://site.example/ok", "https://site.example/cancel");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 25, 13, 0, 0, TimeSpan.Zero));
    private readonly StripeStub _stripe = new();

    private static PaymentOrder Order(string email = "sarah@example.com", string? sessionId = null, string? courseCode = "MAT-FASH") => new()
    {
        Id = Guid.Parse("11111111-2222-4333-8444-555555555555"),
        Reference = "CC-ABC234",
        SubjectKind = PaymentSubjectKind.Student,
        SubjectReference = "21004567",
        SubjectFirstName = "Ellie",
        CourseId = 374,
        CourseTitle = "Garment Studies",
        CourseCode = courseCode,
        ItemKey = Guid.Parse("0f0e5d1c-1a7b-4d61-9c43-5b2b3f6a1001"),
        ItemName = "Materials fee",
        ItemType = PayableItemType.MaterialsFee,
        AmountPence = 4500,
        PayerEmail = email,
        PayerName = "Sarah Turner",
        Status = OrderStatus.Pending,
        GatewaySessionId = sessionId
    };

    private StripePaymentGateway Gateway(int pendingMinutes = 30)
    {
        var client = new StripeClient(new StripeClientOptions
        {
            ApiKey = "sk_test_unit",
            HttpClient = new SystemNetHttpClient(new HttpClient(_stripe), maxNetworkRetries: 0)
        });
        return new StripePaymentGateway(
            client,
            Options.Create(new StripeOptions { SecretKey = "sk_test_unit" }),
            Options.Create(new PaymentOptions { PendingOrderMinutes = pendingMinutes }),
            _clock,
            NullLogger<StripePaymentGateway>.Instance);
    }

    [Theory]
    [InlineData(30, 31)]
    [InlineData(45, 46)]
    [InlineData(10, 31)]
    [InlineData(0, 31)]
    [InlineData(5000, 24 * 60)]
    public void A_session_expires_after_the_order_hold_but_within_Stripes_limits(int pendingMinutes, int expectedMinutesFromNow)
    {
        var now = new DateTimeOffset(2026, 10, 25, 13, 0, 0, TimeSpan.Zero);

        var expires = StripePaymentGateway.SessionExpiresAt(now, pendingMinutes);

        Assert.Equal(now.UtcDateTime.AddMinutes(expectedMinutesFromNow), expires);
        Assert.True(expires - now.UtcDateTime >= TimeSpan.FromMinutes(30));
        Assert.True(expires - now.UtcDateTime <= TimeSpan.FromHours(24));
    }

    [Fact]
    public async Task The_checkout_request_carries_the_expiry_and_an_idempotency_key()
    {
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_1"));

        var checkout = await Gateway(pendingMinutes: 45).CreateCheckoutAsync(Order(), Urls);

        var request = Assert.Single(_stripe.Requests);
        var expectedExpiry = new DateTimeOffset(_clock.Now.UtcDateTime.AddMinutes(46)).ToUnixTimeSeconds();
        Assert.Equal("cs_test_1", checkout.SessionId);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains($"expires_at={expectedExpiry}", request.Body, StringComparison.Ordinal);
        Assert.Equal(StripePaymentGateway.IdempotencyKey(Order()), request.IdempotencyKey);
    }

    [Fact]
    public void The_idempotency_key_changes_when_the_payer_email_or_amount_changes_but_not_on_a_retry()
    {
        var original = StripePaymentGateway.IdempotencyKey(Order());
        var retry = StripePaymentGateway.IdempotencyKey(Order());
        var newEmail = StripePaymentGateway.IdempotencyKey(Order(email: "someone.else@example.com"));
        var sameEmailDifferentCase = StripePaymentGateway.IdempotencyKey(Order(email: " Sarah@Example.com "));
        var newAmount = Order();
        newAmount.AmountPence = 5000;

        Assert.Equal(original, retry);
        Assert.Equal(original, sameEmailDifferentCase);
        Assert.NotEqual(original, newEmail);
        Assert.NotEqual(original, StripePaymentGateway.IdempotencyKey(newAmount));
        Assert.StartsWith("checkout-11111111-2222-4333-8444-555555555555-", original, StringComparison.Ordinal);
        Assert.True(original.Length < 255);
    }

    [Fact]
    public void Replacing_an_earlier_session_gives_a_new_key_so_Stripe_cannot_replay_the_old_session()
    {
        Assert.NotEqual(
            StripePaymentGateway.IdempotencyKey(Order()),
            StripePaymentGateway.IdempotencyKey(Order(sessionId: "cs_test_old")));
    }

    [Fact]
    public async Task An_open_matching_session_is_reused_without_creating_another()
    {
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_old", email: "sarah@example.com"));

        var checkout = await Gateway().CreateCheckoutAsync(Order(sessionId: "cs_test_old"), Urls);

        var request = Assert.Single(_stripe.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("cs_test_old", checkout.SessionId);
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_old", checkout.RedirectUrl);
    }

    [Fact]
    public async Task A_session_for_a_different_payer_is_closed_and_replaced_under_a_new_key()
    {
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_old", email: "old.payer@example.com"));
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_old", status: "expired", email: "old.payer@example.com"));
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_new"));
        var order = Order(email: "sarah@example.com", sessionId: "cs_test_old");

        var checkout = await Gateway().CreateCheckoutAsync(order, Urls);

        Assert.Equal("cs_test_new", checkout.SessionId);
        Assert.Collection(
            _stripe.Requests,
            get => Assert.Equal(HttpMethod.Get, get.Method),
            expire => Assert.EndsWith("/v1/checkout/sessions/cs_test_old/expire", expire.Path, StringComparison.Ordinal),
            create => Assert.Equal(StripePaymentGateway.IdempotencyKey(order), create.IdempotencyKey));
    }

    [Fact]
    public async Task A_session_that_is_no_longer_open_is_replaced()
    {
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_old", status: "expired"));
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_new"));

        var checkout = await Gateway().CreateCheckoutAsync(Order(sessionId: "cs_test_old"), Urls);

        Assert.Equal("cs_test_new", checkout.SessionId);
        Assert.Equal(2, _stripe.Requests.Count);
        Assert.Equal(HttpMethod.Post, _stripe.Requests[1].Method);
    }

    [Fact]
    public async Task A_Stripe_error_becomes_a_payment_gateway_exception_with_a_safe_message()
    {
        _stripe.Enqueue(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","message":"secret internal detail sk_test_leak"}}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => Gateway().CreateCheckoutAsync(Order(), Urls));

        Assert.Equal(PaymentGatewayException.DefaultMessage, ex.Message);
        Assert.DoesNotContain("sk_test", ex.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<StripeException>(ex.InnerException);
    }

    [Fact]
    public async Task A_Stripe_error_while_reading_the_status_is_mapped_too()
    {
        _stripe.Enqueue(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","message":"nope"}}""");

        var ex = await Assert.ThrowsAsync<PaymentGatewayException>(() => Gateway().GetStatusAsync("cs_test_1"));

        Assert.IsAssignableFrom<StripeException>(ex.InnerException);
    }

    [Fact]
    public async Task Expiring_a_session_calls_Stripe_and_swallows_its_errors()
    {
        _stripe.Enqueue(HttpStatusCode.OK, StripeStub.Session("cs_test_1", status: "expired"));
        _stripe.Enqueue(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","message":"Only open sessions can be expired."}}""");

        await Gateway().ExpireAsync("cs_test_1");
        await Gateway().ExpireAsync("cs_test_2");

        Assert.Equal(2, _stripe.Requests.Count);
        Assert.EndsWith("/v1/checkout/sessions/cs_test_1/expire", _stripe.Requests[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Metadata_course_code_falls_back_to_the_course_id()
    {
        var withCode = StripePaymentGateway.Metadata(Order(courseCode: "MAT-FASH"));
        var withoutCode = StripePaymentGateway.Metadata(Order(courseCode: null));
        var blankCode = StripePaymentGateway.Metadata(Order(courseCode: "  "));

        Assert.Equal("MAT-FASH", withCode["course_code"]);
        Assert.Equal("C374", withoutCode["course_code"]);
        Assert.Equal("C374", blankCode["course_code"]);
    }

    [Fact]
    public async Task The_demo_gateway_reuses_its_session_and_expiring_it_does_nothing()
    {
        var demo = new DemoPaymentGateway();

        var first = await demo.CreateCheckoutAsync(Order(), Urls);
        var again = await demo.CreateCheckoutAsync(Order(sessionId: first.SessionId), Urls);
        await demo.ExpireAsync(first.SessionId);

        Assert.Equal(first.SessionId, again.SessionId);
        Assert.Equal("/pay/test-checkout/CC-ABC234", again.RedirectUrl);
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string Body, string? IdempotencyKey);

    private sealed class StripeStub : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Json)> _responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string json) => _responses.Enqueue((status, json));

        public static string Session(string id, string status = "open", string email = "sarah@example.com", int amount = 4500) =>
            $$"""
            {"id":"{{id}}","object":"checkout.session","status":"{{status}}","payment_status":"unpaid","amount_total":{{amount}},
             "customer_email":"{{email}}","url":"https://checkout.stripe.com/c/pay/{{id}}"}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : Uri.UnescapeDataString(await request.Content.ReadAsStringAsync(cancellationToken));
            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, body, key));

            var (status, json) = _responses.Count > 0
                ? _responses.Dequeue()
                : throw new InvalidOperationException("Unexpected extra Stripe request: " + request.RequestUri);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
