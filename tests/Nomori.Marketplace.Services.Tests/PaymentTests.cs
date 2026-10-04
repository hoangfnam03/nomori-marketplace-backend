using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Services.Payments;

namespace Nomori.Marketplace.Services.Tests;

public sealed class PaymentTests
{
    private const string Secret = "unit-test-sandbox-secret";
    private const int Admin = 1;
    private const int Buyer = 100;

    private sealed class FakePaymentStore : IPaymentStore
    {
        private int nextId = 1;

        public List<PaymentMethodSetting> Methods { get; } =
        [
            new() { SystemName = "cod", Enabled = true, DisplayOrder = 0 },
            new() { SystemName = "sandbox", Enabled = true, DisplayOrder = 100 }
        ];

        public List<PaymentTransaction> Payments { get; } = [];
        public List<(string Provider, string EventId, string Outcome)> Events { get; } = [];

        /// <summary>Runs just before a status change is checked, so a test can play another request that got there first.</summary>
        public Action<PaymentTransaction>? BeforeChange { get; set; }

        public Task<IReadOnlyList<PaymentMethodSetting>> GetMethodSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentMethodSetting>>(Methods.ToList());

        public Task<bool> UpdateMethodSettingAsync(PaymentMethodSetting setting, CancellationToken cancellationToken) =>
            Task.FromResult(Methods.Any(m => m.SystemName == setting.SystemName));

        public Task<PaymentTransaction?> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.Id == id));

        public Task<PaymentTransaction?> GetByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.IdempotencyKey == idempotencyKey));

        public Task<PaymentTransaction?> GetByProviderReferenceAsync(string method, string providerReference, CancellationToken cancellationToken) =>
            Task.FromResult(Payments.FirstOrDefault(p => p.Method == method && p.ProviderReference == providerReference));

        public Task<int> InsertAsync(PaymentTransaction payment, CancellationToken cancellationToken)
        {
            if (Payments.Any(p => p.IdempotencyKey == payment.IdempotencyKey)) return Task.FromResult(0);
            payment.Id = nextId++;
            Payments.Add(payment);
            return Task.FromResult(payment.Id);
        }

        public Task<bool> TryChangeAsync(
            int id, PaymentStatus expected, PaymentStatus status, decimal refundedAmount, string? providerReference, string? failureCode,
            DateTime nowUtc, CancellationToken cancellationToken)
        {
            var payment = Payments.Single(p => p.Id == id);
            BeforeChange?.Invoke(payment);
            if (payment.Status != expected) return Task.FromResult(false);
            (payment.Status, payment.RefundedAmount, payment.ProviderReference, payment.FailureCode, payment.UpdatedOnUtc) =
                (status, refundedAmount, providerReference, failureCode, nowUtc);
            return Task.FromResult(true);
        }

        public Task<PagedResult<PaymentTransaction>> GetPagedAsync(PaymentQuery query, CancellationToken cancellationToken)
        {
            var all = Payments.Where(p => query.Status is null || p.Status == query.Status).OrderByDescending(p => p.Id).ToList();
            return Task.FromResult(new PagedResult<PaymentTransaction>(
                all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
        }

        public Task<bool> TryAddEventAsync(string provider, string eventId, string type, int? transactionId, string outcome, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (Events.Any(e => e.Provider == provider && e.EventId == eventId)) return Task.FromResult(false);
            Events.Add((provider, eventId, outcome));
            return Task.FromResult(true);
        }
    }

    /// <summary>A gateway that can be told to fail.</summary>
    private sealed class FlakyGateway : IPaymentProvider
    {
        public bool FailInitiate { get; set; }
        public bool FailCapture { get; set; }
        public string SystemName => "flaky";
        public string DisplayName => "Flaky";
        public PaymentProviderKind Kind => PaymentProviderKind.Gateway;

        public Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult(FailInitiate ? PaymentProviderResult.Fail("declined") : PaymentProviderResult.Ok("flk_1"));

        public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
            Task.FromResult(FailCapture ? PaymentProviderResult.Fail("down") : PaymentProviderResult.Ok());

        public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) => Task.FromResult(PaymentProviderResult.Ok());
        public Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken) => Task.FromResult(PaymentProviderResult.Ok());
        public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers) => null;
    }

    private sealed class Fixture
    {
        public FakePaymentStore Store { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public SandboxPaymentProvider Sandbox { get; } = new(Secret);
        public FlakyGateway Flaky { get; } = new();

        public Fixture() => Store.Methods.Add(new PaymentMethodSetting { SystemName = "flaky", Enabled = true });

        public PaymentService Create(bool withSandbox = true) => new(
            Store, withSandbox ? [new CashOnDeliveryProvider(), Sandbox, Flaky] : [new CashOnDeliveryProvider(), Flaky],
            new FakePrimaryCurrency(), Audit, new TestClock());

        public async Task<PaymentTransaction> PayAsync(string method = "sandbox", decimal amount = 10, string key = "k1")
        {
            var result = await Create().CreateAsync(New(method, amount, key), CancellationToken.None);
            return result.Value!;
        }
    }

    private static CreatePaymentCommand New(string? method = "sandbox", decimal amount = 10, string? key = "k1", int referenceId = 7) =>
        new(PaymentReferenceTypes.Checkout, referenceId, key, method, amount, Buyer);

    private static Dictionary<string, string> Signed(SandboxPaymentProvider sandbox, string body) =>
        new Dictionary<string, string> { [SandboxPaymentProvider.SignatureHeader] = sandbox.Sign(body) };

    private static string Event(string id, string type, string reference) =>
        $$"""{"id":"{{id}}","type":"{{type}}","reference":"{{reference}}"}""";

    // ---- Pure rules ----

    [Theory]
    [InlineData(PaymentStatus.Pending, true, true, false, false)]
    [InlineData(PaymentStatus.Authorized, true, true, false, false)]
    [InlineData(PaymentStatus.Paid, false, false, true, false)]
    [InlineData(PaymentStatus.PartiallyRefunded, false, false, true, false)]
    [InlineData(PaymentStatus.Refunded, false, false, false, true)]
    [InlineData(PaymentStatus.Voided, false, false, false, true)]
    [InlineData(PaymentStatus.Failed, false, false, false, true)]
    public void EachStatusAllowsOnlyItsOperations(PaymentStatus status, bool capture, bool voided, bool refund, bool final)
    {
        Assert.Equal(capture, PaymentRules.CanCapture(status));
        Assert.Equal(voided, PaymentRules.CanVoid(status));
        Assert.Equal(refund, PaymentRules.CanRefund(status));
        Assert.Equal(final, PaymentRules.IsFinal(status));
    }

    [Theory]
    [InlineData(10, 0, 3, PaymentStatus.PartiallyRefunded)]
    [InlineData(10, 3, 7, PaymentStatus.Refunded)]
    [InlineData(10, 0, 10, PaymentStatus.Refunded)]
    public void TheWholeAmountRefundedIsFinal(double paid, double refunded, double amount, PaymentStatus expected) =>
        Assert.Equal(expected, PaymentRules.AfterRefund((decimal)paid, (decimal)refunded, (decimal)amount));

    [Fact]
    public void StatusesRoundTripThroughTheirWireNames()
    {
        foreach (var status in Enum.GetValues<PaymentStatus>())
        {
            Assert.True(PaymentRules.TryParseWire(PaymentRules.ToWire(status), out var parsed));
            Assert.Equal(status, parsed);
        }
        Assert.False(PaymentRules.TryParseWire("nope", out _));
    }

    [Fact]
    public void CallbacksNeedTheRightStartingStatus()
    {
        Assert.True(PaymentRules.ForCallback("captured")!.Value.From(PaymentStatus.Authorized));
        Assert.False(PaymentRules.ForCallback("captured")!.Value.From(PaymentStatus.Refunded));
        Assert.False(PaymentRules.ForCallback("failed")!.Value.From(PaymentStatus.Paid));
        Assert.Null(PaymentRules.ForCallback("exploded"));
    }

    // ---- Methods ----

    [Fact]
    public async Task OnlyEnabledAndRegisteredMethodsAreOffered()
    {
        var f = new Fixture();
        f.Store.Methods.Single(m => m.SystemName == "flaky").Enabled = false;

        Assert.Equal(["cod", "sandbox"], (await f.Create().GetAvailableMethodsAsync(CancellationToken.None)).Select(m => m.SystemName));
        // No provider means the table row alone offers nothing.
        Assert.Equal(["cod"], (await f.Create(withSandbox: false).GetAvailableMethodsAsync(CancellationToken.None)).Select(m => m.SystemName));
    }

    [Fact]
    public async Task AMethodWithoutAProviderCannotBeSwitchedOn()
    {
        var f = new Fixture();
        f.Store.Methods.Single(m => m.SystemName == "sandbox").Enabled = false;

        var refused = await f.Create(withSandbox: false).UpdateMethodAsync("sandbox", true, 5, Admin, CancellationToken.None);
        var allowed = await f.Create().UpdateMethodAsync("sandbox", true, 5, Admin, CancellationToken.None);

        Assert.Equal(PaymentErrors.MethodUnavailable, refused.ErrorCode);
        Assert.True(allowed.Succeeded);
        Assert.Contains("payment.method_updated", f.Audit.Events);
    }

    [Fact]
    public async Task UpdatingAMethodChecksTheNameAndTheOrder()
    {
        var f = new Fixture();

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateMethodAsync("ghost", true, 0, Admin, CancellationToken.None)).ErrorCode);
        Assert.Contains("displayOrder", (await f.Create().UpdateMethodAsync("cod", true, -1, Admin, CancellationToken.None)).Errors.Keys);
    }

    // ---- Create ----

    [Fact]
    public async Task OfflinePaymentsStartPendingAndGatewaysStartAuthorized()
    {
        var f = new Fixture();

        var cod = await f.PayAsync("cod", key: "a");
        var sandbox = await f.PayAsync("sandbox", key: "b");

        Assert.Equal((PaymentStatus.Pending, null), (cod.Status, cod.ProviderReference));
        Assert.Equal(PaymentStatus.Authorized, sandbox.Status);
        Assert.StartsWith("sbx_", sandbox.ProviderReference);
        Assert.Equal(("USD", 10m, Buyer), (sandbox.CurrencyCode, sandbox.Amount, sandbox.CustomerId));
        Assert.Contains("payment.created", f.Audit.Events);
    }

    [Theory]
    [InlineData("cod", 0, "k", "amount")]
    [InlineData("cod", -5, "k", "amount")]
    [InlineData("cod", 10.123, "k", "amount")]
    [InlineData("cod", 2_000_000_000, "k", "amount")]
    [InlineData("cod", 10, "", "idempotencyKey")]
    [InlineData("", 10, "k", "method")]
    public async Task InvalidPaymentsAreRefusedWithTheFieldName(string method, double amount, string key, string field)
    {
        var f = new Fixture();

        var result = await f.Create().CreateAsync(New(method, (decimal)amount, key), CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(f.Store.Payments);
    }

    [Fact]
    public async Task TheKeyIsLimitedInLength()
    {
        var f = new Fixture();

        var result = await f.Create().CreateAsync(New(key: new string('k', 101)), CancellationToken.None);

        Assert.Contains("idempotencyKey", result.Errors.Keys);
    }

    [Fact]
    public async Task DisabledOrUnknownMethodsAreRefused()
    {
        var f = new Fixture();
        f.Store.Methods.Single(m => m.SystemName == "cod").Enabled = false;

        Assert.Equal(PaymentErrors.MethodUnavailable, (await f.Create().CreateAsync(New("cod"), CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrors.MethodUnavailable, (await f.Create().CreateAsync(New("paypal", key: "z"), CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrors.MethodUnavailable, (await f.Create(withSandbox: false).CreateAsync(New("sandbox", key: "y"), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task TheSameRequestAgainReturnsTheSamePayment()
    {
        var f = new Fixture();

        var first = await f.Create().CreateAsync(New(), CancellationToken.None);
        var again = await f.Create().CreateAsync(New(), CancellationToken.None);

        Assert.True(again.Succeeded);
        Assert.Equal(first.Value!.Id, again.Value!.Id);
        Assert.Single(f.Store.Payments);
        Assert.Single(f.Audit.Events, e => e == "payment.created");
    }

    [Fact]
    public async Task TheSameKeyWithDifferentValuesIsAConflict()
    {
        var f = new Fixture();
        await f.PayAsync(amount: 10);

        Assert.Equal(PaymentErrors.IdempotencyConflict, (await f.Create().CreateAsync(New(amount: 11), CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrors.IdempotencyConflict, (await f.Create().CreateAsync(New(method: "cod"), CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrors.IdempotencyConflict, (await f.Create().CreateAsync(New(referenceId: 8), CancellationToken.None)).ErrorCode);
        Assert.Single(f.Store.Payments);
    }

    [Fact]
    public async Task AProviderFailureIsStoredAsFailedAndRepeatsTheFailure()
    {
        var f = new Fixture();
        f.Flaky.FailInitiate = true;

        var first = await f.Create().CreateAsync(New("flaky"), CancellationToken.None);
        var again = await f.Create().CreateAsync(New("flaky"), CancellationToken.None);

        Assert.Equal(PaymentErrors.ProviderFailed, first.ErrorCode);
        Assert.Equal(PaymentErrors.ProviderFailed, again.ErrorCode);
        var stored = Assert.Single(f.Store.Payments);
        Assert.Equal((PaymentStatus.Failed, "declined"), (stored.Status, stored.FailureCode));
        Assert.Contains("payment.failed", f.Audit.Events);
    }

    // ---- Capture, void, refund ----

    [Fact]
    public async Task CapturingAnAuthorizedPaymentPaysIt()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();

        var result = await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None);

        Assert.Equal(PaymentStatus.Paid, result.Value!.Status);
        Assert.Contains("payment.captured", f.Audit.Events);
    }

    [Fact]
    public async Task CashIsCapturedWhenAnAdministratorRecordsIt()
    {
        var f = new Fixture();
        var payment = await f.PayAsync("cod");

        Assert.Equal(PaymentStatus.Paid, (await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None)).Value!.Status);
    }

    [Fact]
    public async Task OperationsOnTheWrongStatusAreRefused()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        await f.Create().VoidAsync(payment.Id, Admin, CancellationToken.None);

        Assert.Equal(PaymentErrors.InvalidState, (await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrors.InvalidState, (await f.Create().VoidAsync(payment.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrors.InvalidState, (await f.Create().RefundAsync(payment.Id, 1, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
    }

    [Fact]
    public async Task AFailingProviderKeepsTheStatus()
    {
        var f = new Fixture();
        var payment = (await f.Create().CreateAsync(New("flaky"), CancellationToken.None)).Value!;
        f.Flaky.FailCapture = true;

        var result = await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None);

        Assert.Equal(PaymentErrors.ProviderFailed, result.ErrorCode);
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.DoesNotContain("payment.captured", f.Audit.Events);
    }

    [Fact]
    public async Task ALostRaceIsAnInvalidStateAndChangesNothing()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        // Another request voids the payment between the read and the change.
        f.Store.BeforeChange = p => { f.Store.BeforeChange = null; p.Status = PaymentStatus.Voided; };

        var result = await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None);

        Assert.Equal(PaymentErrors.InvalidState, result.ErrorCode);
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.DoesNotContain("payment.captured", f.Audit.Events);
    }

    [Fact]
    public async Task RefundsCanBePartialAndNeverExceedThePayment()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None);

        var partial = await f.Create().RefundAsync(payment.Id, 3, Admin, CancellationToken.None);
        // The fake store hands out the stored object, so what it says is read before the next change.
        Assert.Equal((PaymentStatus.PartiallyRefunded, 3m), (partial.Value!.Status, partial.Value.RefundedAmount));
        var tooMuch = await f.Create().RefundAsync(payment.Id, 7.01m, Admin, CancellationToken.None);
        var rest = await f.Create().RefundAsync(payment.Id, 7, Admin, CancellationToken.None);

        Assert.Equal(PaymentErrors.RefundExceeds, tooMuch.ErrorCode);
        Assert.Equal((PaymentStatus.Refunded, 10m), (rest.Value!.Status, rest.Value.RefundedAmount));
        Assert.Equal(PaymentErrors.InvalidState, (await f.Create().RefundAsync(payment.Id, 1, Admin, CancellationToken.None)).ErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.005)]
    public async Task RefundAmountsAreChecked(double amount)
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        await f.Create().CaptureAsync(payment.Id, Admin, CancellationToken.None);

        var result = await f.Create().RefundAsync(payment.Id, (decimal)amount, Admin, CancellationToken.None);

        Assert.Contains("amount", result.Errors.Keys);
        Assert.Equal(0m, payment.RefundedAmount);
    }

    [Fact]
    public async Task APaymentThatDoesNotExistIsNotFound()
    {
        var f = new Fixture();

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().CaptureAsync(99, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().VoidAsync(99, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().RefundAsync(99, 1, Admin, CancellationToken.None)).ErrorCode);
        Assert.Null(await f.Create().GetPaymentAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task ThePaymentListFiltersByStatusAndPages()
    {
        var f = new Fixture();
        await f.PayAsync(key: "a");
        var b = await f.PayAsync(key: "b");
        await f.PayAsync("cod", key: "c");
        await f.Create().VoidAsync(b.Id, Admin, CancellationToken.None);

        var voided = await f.Create().GetPaymentsAsync(new PaymentQuery(PaymentStatus.Voided, 1, 10), CancellationToken.None);
        var page = await f.Create().GetPaymentsAsync(new PaymentQuery(null, 0, 500), CancellationToken.None);

        Assert.Equal([b.Id], voided.Items.Select(p => p.Id));
        Assert.Equal((3, 1, 100), (page.TotalCount, page.Page, page.PageSize));
    }

    // ---- Callbacks ----

    [Fact]
    public async Task ASignedCallbackIsAppliedOnceAndAReplayChangesNothing()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        var body = Event("evt_1", "captured", payment.ProviderReference!);

        var first = await f.Create().HandleCallbackAsync("sandbox", body, Signed(f.Sandbox, body), CancellationToken.None);
        var replay = await f.Create().HandleCallbackAsync("sandbox", body, Signed(f.Sandbox, body), CancellationToken.None);

        Assert.Equal((CallbackOutcome.Applied, CallbackOutcome.Replay), (first, replay));
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Single(f.Store.Events);
        Assert.Single(f.Audit.Events, e => e == "payment.callback_applied");
    }

    [Fact]
    public async Task ABadSignatureIsRejectedAndNothingIsStored()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        var body = Event("evt_1", "captured", payment.ProviderReference!);
        var altered = body.Replace("captured", "voided", StringComparison.Ordinal);

        Assert.Equal(CallbackOutcome.Rejected, await f.Create().HandleCallbackAsync("sandbox", altered, Signed(f.Sandbox, body), CancellationToken.None));
        Assert.Equal(CallbackOutcome.Rejected, await f.Create().HandleCallbackAsync("sandbox", body, new Dictionary<string, string>(), CancellationToken.None));
        Assert.Equal(CallbackOutcome.Rejected, await f.Create().HandleCallbackAsync("sandbox", body,
            new Dictionary<string, string> { [SandboxPaymentProvider.SignatureHeader] = new SandboxPaymentProvider("another-secret-entirely").Sign(body) }, CancellationToken.None));
        Assert.Equal(PaymentStatus.Authorized, payment.Status);
        Assert.Empty(f.Store.Events);
    }

    [Fact]
    public async Task AnUnknownProviderOrOneWithoutCallbacksIsRejected()
    {
        var f = new Fixture();
        var body = Event("evt_1", "captured", "x");

        Assert.Equal(CallbackOutcome.Rejected, await f.Create().HandleCallbackAsync("paypal", body, Signed(f.Sandbox, body), CancellationToken.None));
        Assert.Equal(CallbackOutcome.Rejected, await f.Create().HandleCallbackAsync("cod", body, Signed(f.Sandbox, body), CancellationToken.None));
        Assert.Equal(CallbackOutcome.Rejected, await f.Create(withSandbox: false).HandleCallbackAsync("sandbox", body, Signed(f.Sandbox, body), CancellationToken.None));
    }

    [Fact]
    public async Task ASignedBodyThatIsNotAnEventIsRejected()
    {
        var f = new Fixture();

        foreach (var body in new[] { "not json", "[]", """{"id":"e"}""", """{"id":1,"type":"captured","reference":"r"}""" })
            Assert.Equal(CallbackOutcome.Rejected, await f.Create().HandleCallbackAsync("sandbox", body, Signed(f.Sandbox, body), CancellationToken.None));
        Assert.Empty(f.Store.Events);
    }

    [Fact]
    public async Task EventsForUnknownPaymentsOrIllegalTransitionsAreStoredAsIgnored()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        await f.Create().VoidAsync(payment.Id, Admin, CancellationToken.None);

        var unknown = Event("evt_1", "captured", "sbx_nobody");
        var illegal = Event("evt_2", "captured", payment.ProviderReference!);
        var strange = Event("evt_3", "exploded", payment.ProviderReference!);

        Assert.Equal(CallbackOutcome.Ignored, await f.Create().HandleCallbackAsync("sandbox", unknown, Signed(f.Sandbox, unknown), CancellationToken.None));
        Assert.Equal(CallbackOutcome.Ignored, await f.Create().HandleCallbackAsync("sandbox", illegal, Signed(f.Sandbox, illegal), CancellationToken.None));
        Assert.Equal(CallbackOutcome.Ignored, await f.Create().HandleCallbackAsync("sandbox", strange, Signed(f.Sandbox, strange), CancellationToken.None));
        Assert.Equal(PaymentStatus.Voided, payment.Status);
        Assert.All(f.Store.Events, e => Assert.Equal("ignored", e.Outcome));
        Assert.Equal(3, f.Audit.Events.Count(e => e == "payment.callback_ignored"));
    }

    [Fact]
    public async Task AFailedEventMarksThePaymentFailed()
    {
        var f = new Fixture();
        var payment = await f.PayAsync();
        var body = Event("evt_9", "failed", payment.ProviderReference!);

        await f.Create().HandleCallbackAsync("sandbox", body, Signed(f.Sandbox, body), CancellationToken.None);

        Assert.Equal((PaymentStatus.Failed, "provider_reported"), (payment.Status, payment.FailureCode));
    }

    // ---- Sandbox provider ----

    [Fact]
    public void TheSandboxComparesTheWholeSignature()
    {
        var sandbox = new SandboxPaymentProvider(Secret);
        var body = Event("e", "captured", "r");
        var good = sandbox.Sign(body);

        Assert.NotNull(sandbox.ParseCallback(body, new Dictionary<string, string> { ["x-nomori-signature"] = good }));
        Assert.NotNull(sandbox.ParseCallback(body, new Dictionary<string, string> { ["X-NOMORI-SIGNATURE"] = good.ToUpperInvariant() }));
        Assert.Null(sandbox.ParseCallback(body, new Dictionary<string, string> { ["X-Nomori-Signature"] = good[..^1] }));
        Assert.Null(sandbox.ParseCallback(body, new Dictionary<string, string> { ["X-Nomori-Signature"] = string.Empty }));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("short", false)]
    [InlineData("a-secret-of-sixteen", true)]
    public void TheSandboxNeedsALongEnoughSecret(string secret, bool configured) =>
        Assert.Equal(configured, new SandboxPaymentOptions { Secret = secret }.IsConfigured);
}
