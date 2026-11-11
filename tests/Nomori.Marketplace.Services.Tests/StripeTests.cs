using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Services.Payments;

namespace Nomori.Marketplace.Services.Tests;

public sealed class StripeTests
{
    private const string WebhookSecret = "whsec_test_secret_test_secret";
    private const int Buyer = 100;

    // The test clock is 2026-01-01 00:00 UTC.
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly StripePaymentOptions Options = new()
    {
        SecretKey = "sk_test_abc", WebhookSecret = WebhookSecret, ApiBase = "https://api.stripe.test", ReturnBaseUrl = "https://shop.test/"
    };

    private sealed class VndCurrency : IPrimaryCurrencyProvider
    {
        public Task<Currency> GetPrimaryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Currency { Id = 1, Code = "VND", Name = "Dong", Symbol = "₫", DecimalPlaces = 0, IsPrimary = true });
    }

    /// <summary>Stripe's API: answers each path with a set JSON and keeps what it was sent.</summary>
    private sealed class FakeStripe : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = new()
        {
            ["POST /v1/checkout/sessions"] = """{"id":"cs_test_1","url":"https://checkout.stripe.test/c/pay/cs_test_1"}""",
            ["GET /v1/checkout/sessions/cs_test_1"] =
                """{"id":"cs_test_1","object":"checkout.session","payment_status":"paid","amount_total":150000,"currency":"vnd","payment_intent":"pi_1"}""",
            ["POST /v1/refunds"] = """{"id":"re_1","status":"succeeded"}""",
            // A VND charge settled in a USD balance: 150,000 VND at 0.0000395 USD each is 5.93 USD; Stripe kept 0.47 USD of it.
            ["GET /v1/payment_intents/pi_1"] = """
                {"id":"pi_1","latest_charge":{"id":"ch_1","currency":"vnd",
                 "balance_transaction":{"id":"txn_1","amount":593,"fee":47,"currency":"usd","exchange_rate":0.0000395}}}
                """
        };

        public List<(string Key, Dictionary<string, string> Form, HttpRequestMessage Request)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            var form = request.Content is null
                ? []
                : (await request.Content.ReadAsStringAsync(cancellationToken)).Split('&')
                    .Select(p => p.Split('=', 2)).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            Sent.Add((key, form, request));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answers[key], Encoding.UTF8, "application/json") };
        }
    }

    private static StripePaymentProvider Provider(FakeStripe api) => new(Options, new HttpClient(api), new TestClock());

    private static PaymentTransaction Payment(PaymentStatus status = PaymentStatus.Pending, string currency = "VND") => new()
    {
        Id = 9, ReferenceType = PaymentReferenceTypes.Order, ReferenceId = 42, CustomerId = Buyer, Method = StripePaymentProvider.Name,
        Amount = 150_000m, CurrencyCode = currency, Status = status, ProviderReference = "cs_test_1",
        GatewayRedirectUrl = "https://checkout.stripe.test/c/pay/cs_test_1", GatewayTransactionId = "pi_1", CreatedOnUtc = Now
    };

    /// <summary>A webhook as Stripe sends it: the event, and its Stripe-Signature for a time.</summary>
    private static (string Body, Dictionary<string, string> Headers) Event(
        string type, string paymentStatus = "paid", long amount = 150_000, DateTime? at = null, string secret = WebhookSecret, string id = "evt_1")
    {
        var body = new JsonObject
        {
            ["id"] = id, ["type"] = type,
            ["data"] = new JsonObject
            {
                ["object"] = new JsonObject
                {
                    ["id"] = "cs_test_1", ["payment_status"] = paymentStatus, ["amount_total"] = amount, ["currency"] = "vnd", ["payment_intent"] = "pi_1"
                }
            }
        }.ToJsonString();
        var timestamp = new DateTimeOffset(at ?? Now).ToUnixTimeSeconds();
        return (body, new Dictionary<string, string> { ["Stripe-Signature"] = $"t={timestamp},v1={StripePaymentProvider.Sign(secret, $"{timestamp}.{body}")}" });
    }

    // ---- Starting a payment ----

    [Fact]
    public async Task StartingAPaymentCreatesACheckoutSessionInDongAndKeepsItsPage()
    {
        var api = new FakeStripe();

        var started = await Provider(api).InitiateAsync(Payment(), CancellationToken.None);

        Assert.Equal((true, "cs_test_1", "https://checkout.stripe.test/c/pay/cs_test_1"), (started.Succeeded, started.ProviderReference, started.RedirectUrl));
        var (key, form, request) = Assert.Single(api.Sent);
        Assert.Equal("POST /v1/checkout/sessions", key);
        Assert.Equal(("Bearer", "sk_test_abc"), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        Assert.Equal("nomori-payment-9", request.Headers.GetValues("Idempotency-Key").Single());
        // VND has no smaller unit: 150 000 dong is 150000, not 15000000.
        Assert.Equal(("vnd", "150000", "payment"), (form["line_items[0][price_data][currency]"], form["line_items[0][price_data][unit_amount]"], form["mode"]));
        Assert.Equal("https://shop.test/api/v1/payments/stripe/return/9?session_id={CHECKOUT_SESSION_ID}", form["success_url"]);
        Assert.Equal(new DateTimeOffset(Now.AddMinutes(30)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), form["expires_at"]);
    }

    [Fact]
    public async Task ACurrencyWithCentsIsSentInCentsAndARefusalFailsWithStripesCode()
    {
        var api = new FakeStripe();
        await Provider(api).InitiateAsync(Payment(currency: "USD"), CancellationToken.None);
        Assert.Equal("15000000", api.Sent[0].Form["line_items[0][price_data][unit_amount]"]);

        api.Answers["POST /v1/checkout/sessions"] = """{"error":{"code":"amount_too_small","message":"..."}}""";
        Assert.Equal("stripe_amount_too_small", (await Provider(api).InitiateAsync(Payment(), CancellationToken.None)).FailureCode);
    }

    [Fact]
    public void TheCustomerIsSentToTheKeptPageOnlyWhileThePaymentWaits()
    {
        Assert.Equal("https://checkout.stripe.test/c/pay/cs_test_1", Provider(new FakeStripe()).GetRedirectUrl(Payment()));
        Assert.Null(Provider(new FakeStripe()).GetRedirectUrl(Payment(PaymentStatus.Paid)));
    }

    // ---- What Stripe reports ----

    [Fact]
    public void APaidSessionIsACaptureWithItsAmountAndPaymentIntent()
    {
        var (body, headers) = Event("checkout.session.completed");

        var callback = Provider(new FakeStripe()).ParseCallback(body, headers)!;

        Assert.Equal(("evt_1", PaymentCallbackTypes.Captured, "cs_test_1", 150_000m, "pi_1"),
            (callback.EventId, callback.Type, callback.ProviderReference, callback.Amount, callback.GatewayTransactionId));
    }

    [Theory]
    [InlineData("checkout.session.expired", "unpaid", PaymentCallbackTypes.Failed)]
    [InlineData("checkout.session.async_payment_failed", "unpaid", PaymentCallbackTypes.Failed)]
    [InlineData("checkout.session.completed", "unpaid", "ignored")]
    [InlineData("payment_intent.created", "unpaid", "ignored")]
    public void OtherEventsFailThePaymentOrChangeNothing(string type, string status, string expected)
    {
        var (body, headers) = Event(type, status);

        Assert.Equal(expected, Provider(new FakeStripe()).ParseCallback(body, headers)!.Type);
    }

    [Fact]
    public void AnEventNotSignedWithTheSecretOrTooOldIsRefused()
    {
        var provider = Provider(new FakeStripe());
        var (wrongBody, wrongHeaders) = Event("checkout.session.completed", secret: "whsec_other_other_other_other");
        var (oldBody, oldHeaders) = Event("checkout.session.completed", at: Now.AddMinutes(-10));
        var (body, _) = Event("checkout.session.completed");

        Assert.Null(provider.ParseCallback(wrongBody, wrongHeaders));
        Assert.Null(provider.ParseCallback(oldBody, oldHeaders));
        Assert.Null(provider.ParseCallback(body, new Dictionary<string, string>()));
    }

    [Fact]
    public async Task ThePaidSessionTheCustomerCameBackFromIsWrittenAsASignedEvent()
    {
        var api = new FakeStripe();
        var provider = Provider(api);

        var paid = await provider.ReadPaidSessionAsync("cs_test_1", CancellationToken.None);

        var callback = provider.ParseCallback(paid!.Value.Body, paid.Value.Headers)!;
        Assert.Equal((PaymentCallbackTypes.Captured, "cs_test_1", "pi_1"), (callback.Type, callback.ProviderReference, callback.GatewayTransactionId));
        Assert.Equal("GET /v1/checkout/sessions/cs_test_1", Assert.Single(api.Sent).Key);
    }

    [Fact]
    public async Task AnUnpaidOrStrangeSessionGivesNothing()
    {
        var api = new FakeStripe();
        api.Answers["GET /v1/checkout/sessions/cs_test_1"] = """{"id":"cs_test_1","payment_status":"unpaid"}""";

        Assert.Null(await Provider(api).ReadPaidSessionAsync("cs_test_1", CancellationToken.None));
        Assert.Null(await Provider(api).ReadPaidSessionAsync("../v1/charges", CancellationToken.None));
        Assert.Single(api.Sent);
    }

    // ---- Through the payment service ----

    private static PaymentService Service(FakeStripe api)
    {
        var store = new PaymentTests.FakePaymentStore();
        store.Methods.Add(new PaymentMethodSetting { SystemName = StripePaymentProvider.Name, Enabled = true });
        return new PaymentService(store, [Provider(api)], [], new VndCurrency(), new RecordingAuditLog(), new TestClock());
    }

    [Fact]
    public async Task APaymentIsPaidByTheReturnAndTheLaterWebhookChangesNothing()
    {
        var api = new FakeStripe();
        var service = Service(api);
        var created = (await service.CreateAsync(
            new CreatePaymentCommand(PaymentReferenceTypes.Order, 42, "o-42", StripePaymentProvider.Name, 150_000m, Buyer), CancellationToken.None)).Value!;
        Assert.Equal("https://checkout.stripe.test/c/pay/cs_test_1", created.RedirectUrl);

        var paid = (await Provider(api).ReadPaidSessionAsync("cs_test_1", CancellationToken.None))!.Value;
        var byReturn = await service.HandleCallbackAsync(StripePaymentProvider.Name, paid.Body, paid.Headers, CancellationToken.None);
        var (body, headers) = Event("checkout.session.completed");
        var byWebhook = await service.HandleCallbackAsync(StripePaymentProvider.Name, body, headers, CancellationToken.None);

        Assert.Equal((CallbackOutcome.Applied, CallbackOutcome.Ignored), (byReturn, byWebhook));
        var payment = (await service.GetPaymentAsync(created.Id, CancellationToken.None))!;
        Assert.Equal((PaymentStatus.Paid, "pi_1"), (payment.Status, payment.GatewayTransactionId));
    }

    [Fact]
    public async Task AWebhookForAnotherAmountChangesNothing()
    {
        var service = Service(new FakeStripe());
        var created = (await service.CreateAsync(
            new CreatePaymentCommand(PaymentReferenceTypes.Order, 42, "o-42", StripePaymentProvider.Name, 150_000m, Buyer), CancellationToken.None)).Value!;
        var (body, headers) = Event("checkout.session.completed", amount: 1_000);

        Assert.Equal(CallbackOutcome.AmountMismatch, await service.HandleCallbackAsync(StripePaymentProvider.Name, body, headers, CancellationToken.None));
        Assert.Equal(PaymentStatus.Pending, (await service.GetPaymentAsync(created.Id, CancellationToken.None))!.Status);
    }

    // ---- Refunds ----

    [Fact]
    public async Task ARefundNamesThePaymentIntent()
    {
        var api = new FakeStripe();

        var result = await Provider(api).RefundAsync(Payment(PaymentStatus.Paid), 50_000m, CancellationToken.None);

        Assert.True(result.Succeeded);
        var form = Assert.Single(api.Sent).Form;
        Assert.Equal(("pi_1", "50000"), (form["payment_intent"], form["amount"]));
    }

    [Fact]
    public async Task ARefundWithoutThePaymentIntentOrRefusedByStripeFails()
    {
        var noIntent = Payment(PaymentStatus.Paid);
        noIntent.GatewayTransactionId = null;
        Assert.Equal("stripe_no_payment_intent", (await Provider(new FakeStripe()).RefundAsync(noIntent, 1_000m, CancellationToken.None)).FailureCode);

        var api = new FakeStripe();
        api.Answers["POST /v1/refunds"] = """{"error":{"code":"charge_already_refunded"}}""";
        Assert.Equal("stripe_refund_charge_already_refunded", (await Provider(api).RefundAsync(Payment(PaymentStatus.Paid), 1_000m, CancellationToken.None)).FailureCode);
    }

    // ---- The fee ----

    [Fact]
    public async Task TheFeeIsReadFromTheChargeAndBroughtBackToThePaymentsCurrency()
    {
        var api = new FakeStripe();

        var fee = await Provider(api).GetFeeAsync(Payment(PaymentStatus.Paid), CancellationToken.None);

        // 0.47 USD / 0.0000395 = 11,898.73... VND, rounded to the dong.
        Assert.Equal(11_899m, fee);
        var (key, _, request) = Assert.Single(api.Sent);
        Assert.Equal("GET /v1/payment_intents/pi_1", key);
        Assert.Contains("expand", request.RequestUri!.Query);
    }

    [Theory]
    [InlineData("""{"latest_charge":{"currency":"vnd","balance_transaction":{"fee":3500,"currency":"vnd","exchange_rate":null}}}""", 3500)]
    [InlineData("""{"latest_charge":{"currency":"usd","balance_transaction":{"fee":59,"currency":"usd"}}}""", 0.59)]
    public void AFeeInThePaymentsOwnCurrencyNeedsNoRate(string json, double expected) =>
        Assert.Equal((decimal)expected, StripePaymentProvider.FeeOf(JsonNode.Parse(json) as JsonObject));

    [Theory]
    [InlineData("""{"latest_charge":"ch_1"}""")]
    [InlineData("""{"latest_charge":{"balance_transaction":"txn_1"}}""")]
    [InlineData("""{"latest_charge":null}""")]
    [InlineData("""{"error":{"message":"No such payment_intent"}}""")]
    public void NoFeeIsKnownWithoutTheBalanceTransaction(string json) =>
        Assert.Null(StripePaymentProvider.FeeOf(JsonNode.Parse(json) as JsonObject));

    [Fact]
    public async Task APaymentWithoutAPaymentIntentHasNoFeeToAskFor()
    {
        var api = new FakeStripe();

        var payment = Payment(PaymentStatus.Paid);
        payment.GatewayTransactionId = null;
        Assert.Null(await Provider(api).GetFeeAsync(payment, CancellationToken.None));
        Assert.Empty(api.Sent);
    }
}
