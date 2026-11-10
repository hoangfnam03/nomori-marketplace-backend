using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Returns;
using Nomori.Marketplace.Core.Wallets;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Returns;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ReturnTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private const int Buyer = 100;
    private const int OtherBuyer = 101;
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;
    private const int Admin = 1;
    private const int ShopOrderId = 10;
    private const int Line1 = 101;
    private const int Line2 = 102;

    private sealed class ReturnClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    /// <summary>A carrier for returns: none by default (the customer sends the goods), or one that makes or refuses the parcel.</summary>
    internal sealed class FakeShipper : IReturnShipper
    {
        public ReturnShipment? Next { get; set; }
        public bool Throws { get; set; }
        public List<string> Shipped { get; } = [];

        public Task<ReturnShipment?> ShipBackAsync(ReturnRequest request, CancellationToken cancellationToken)
        {
            if (Throws) throw new HttpRequestException("down");
            if (Next is not null) Shipped.Add(request.Number);
            return Task.FromResult(Next);
        }
    }

    /// <summary>Works like the SQL store: the quantity check and the compare-and-set are the same rules in memory. Returns copies, as a database does.</summary>
    internal sealed class FakeReturnStore : IReturnStore
    {
        /// <summary>Puts a return in as it is, without the checks of a new request (for tests of other services).</summary>
        public ReturnRequest Seed(ReturnRequest request)
        {
            request.Id = rows.Count + 1;
            rows.Add(Clone(request));
            return request;
        }

        public Task SetShipmentAsync(int id, ReturnShipment shipment, string payer, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var row = rows.Single(r => r.Id == id);
            row.ReturnTrackingNumber = shipment.TrackingNumber;
            row.ReturnShippingFee = shipment.Succeeded ? shipment.Fee : 0m;
            row.ReturnShippingPaidBy = shipment.Succeeded ? payer : null;
            row.ReturnShipmentError = shipment.Succeeded ? null : shipment.Error;
            row.ReturnShipmentStatus = null;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetShippingRefundAsync(
            int id, int shopOrderId, decimal amount, string? payer, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (amount > 0 && rows.Any(r => r.ShopOrderId == shopOrderId && r.Id != id && r.ShippingRefund > 0)) return Task.FromResult(false);
            var row = rows.Single(r => r.Id == id);
            row.ShippingRefund = amount;
            row.ShippingRefundPaidBy = payer;
            return Task.FromResult(true);
        }

        public Task<int?> FindByTrackingNumberAsync(string trackingNumber, CancellationToken cancellationToken) =>
            Task.FromResult(rows.LastOrDefault(r => r.ReturnTrackingNumber == trackingNumber)?.Id);

        public Task SetShipmentStatusAsync(int id, string status, DateTime nowUtc, CancellationToken cancellationToken)
        {
            rows.Single(r => r.Id == id).ReturnShipmentStatus = status;
            return Task.CompletedTask;
        }

        private readonly List<ReturnRequest> rows = [];
        public Dictionary<int, List<int>> PaymentIds { get; } = [];

        public Task<(ReturnRequest? Request, bool QuantityExceeded)> InsertAsync(ReturnRequest request, CancellationToken cancellationToken)
        {
            var held = HeldFor(request.ShopOrderId);
            var orderLines = Orders.Orders.SelectMany(o => o.ShopOrders).Single(s => s.Id == request.ShopOrderId).Lines.ToDictionary(l => l.Id);
            if (request.Lines.Any(l => l.Quantity > orderLines[l.OrderLineId].Quantity - held.GetValueOrDefault(l.OrderLineId)))
                return Task.FromResult<(ReturnRequest?, bool)>((null, true));

            request.Id = rows.Count + 1;
            request.Number = ReturnRules.NumberFor(request.CreatedOnUtc, request.Id);
            var next = 1;
            foreach (var line in request.Lines) (line.Id, line.ReturnRequestId) = (next++, request.Id);
            rows.Add(Clone(request));
            return Task.FromResult<(ReturnRequest?, bool)>((Clone(request), false));
        }

        public FakeOrderHolder Orders { get; set; } = new();

        private Dictionary<int, int> HeldFor(int shopOrderId) =>
            rows.Where(r => r.ShopOrderId == shopOrderId && ReturnRules.HoldsQuantity(r.Status)).SelectMany(r => r.Lines)
                .GroupBy(l => l.OrderLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

        public Task<IReadOnlyDictionary<int, int>> RefundedQuantitiesAsync(int shopOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(
                rows.Where(r => r.ShopOrderId == shopOrderId && r.Status == ReturnStatus.Refunded).SelectMany(r => r.Lines)
                    .GroupBy(l => l.OrderLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity)));

        public Task<IReadOnlyDictionary<int, int>> HeldQuantitiesAsync(int shopOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(HeldFor(shopOrderId));

        public Task<ReturnRequest?> GetAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult(rows.SingleOrDefault(r => r.Id == id) is { } row ? Clone(row) : null);

        public Task<PagedResult<ReturnRequest>> GetPagedAsync(ReturnQuery query, CancellationToken cancellationToken)
        {
            var all = rows.Where(r => (query.CustomerId is null || r.CustomerId == query.CustomerId)
                && (query.VendorId is null || r.VendorId == query.VendorId) && (query.Status is null || r.Status == query.Status))
                .OrderByDescending(r => r.Id).Select(Clone).ToList();
            return Task.FromResult(new PagedResult<ReturnRequest>(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
        }

        public Task<bool> TryTransitionAsync(ReturnTransition t, CancellationToken cancellationToken)
        {
            var row = rows.Single(r => r.Id == t.Id);
            if (row.Status != t.From) return Task.FromResult(false);
            row.Status = t.To;
            row.UpdatedOnUtc = t.NowUtc;
            row.ResolutionNote = t.ResolutionNote ?? row.ResolutionNote;
            row.Restocked = t.Restocked ?? row.Restocked;
            row.PaymentId = t.To == ReturnStatus.Refunded ? t.PaymentId : t.To == ReturnStatus.Received ? null : row.PaymentId;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(PaymentIds.GetValueOrDefault(orderId) ?? []);

        public ReturnRequest Raw(int id) => rows.Single(r => r.Id == id);

        private static ReturnRequest Clone(ReturnRequest r) => new()
        {
            Id = r.Id, Number = r.Number, ShopOrderId = r.ShopOrderId, OrderId = r.OrderId, ShopOrderNumber = r.ShopOrderNumber, VendorId = r.VendorId,
            ShopName = r.ShopName, CustomerId = r.CustomerId, Status = r.Status, Reason = r.Reason, CustomerNote = r.CustomerNote,
            ResolutionNote = r.ResolutionNote, CurrencyCode = r.CurrencyCode, RefundAmount = r.RefundAmount, Restocked = r.Restocked,
            PaymentId = r.PaymentId, CreatedOnUtc = r.CreatedOnUtc, UpdatedOnUtc = r.UpdatedOnUtc,
            ReturnTrackingNumber = r.ReturnTrackingNumber, ReturnShippingFee = r.ReturnShippingFee, ReturnShippingPaidBy = r.ReturnShippingPaidBy,
            ReturnShipmentStatus = r.ReturnShipmentStatus, ReturnShipmentError = r.ReturnShipmentError,
            ShippingRefund = r.ShippingRefund, ShippingRefundPaidBy = r.ShippingRefundPaidBy,
            Lines = r.Lines.Select(l => new ReturnLine
            {
                Id = l.Id, ReturnRequestId = l.ReturnRequestId, OrderLineId = l.OrderLineId, Name = l.Name, VariantLabel = l.VariantLabel,
                ProductId = l.ProductId, CombinationId = l.CombinationId, Quantity = l.Quantity, Amount = l.Amount
            }).ToList()
        };
    }

    internal sealed class FakeOrderHolder
    {
        public List<Order> Orders { get; } = [];
    }

    /// <summary>The customer's wallet: keeps every credit, once per key; can be made to fail like an unreachable database.</summary>
    private sealed class FakeWallets : IWalletService
    {
        public List<(int CustomerId, decimal Amount, string Key)> Credits { get; } = [];
        public bool Throws { get; set; }

        public Task<WalletPostResult> CreditAsync(
            int customerId, WalletEntryKind kind, decimal amount, string idempotencyKey, string? referenceType, int? referenceId, string? note,
            CancellationToken cancellationToken)
        {
            if (Throws) throw new InvalidOperationException("wallet down");
            if (Credits.Any(c => c.Key == idempotencyKey)) return Task.FromResult(new WalletPostResult(WalletPostOutcome.AlreadyPosted, 0m));
            Credits.Add((customerId, amount, idempotencyKey));
            return Task.FromResult(new WalletPostResult(WalletPostOutcome.Posted, Credits.Where(c => c.CustomerId == customerId).Sum(c => c.Amount)));
        }

        public Task<Wallet> GetMineAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PagedResult<WalletEntry>> GetEntriesAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<PaymentTransaction>> StartTopUpAsync(int customerId, WalletTopUpCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingQueue : IEmailQueueService
    {
        public List<(string Kind, EmailMessage Message)> Queued { get; } = [];

        public Task<CatalogResult<long>> EnqueueAsync(string kind, EmailMessage message, CancellationToken cancellationToken)
        {
            Queued.Add((kind, message));
            return Task.FromResult(CatalogResult.Success((long)Queued.Count));
        }

        public Task<PagedResult<QueuedEmail>> ListAsync(EmailQueueQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, int>> GetCountsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<QueuedEmail>> GetAsync(long id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<QueuedEmail>> RetryAsync(long id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteAsync(long id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public OrderTests.FakeOrderStore Orders { get; } = new();
        public FakeReturnStore Store { get; } = new();
        public FakeStockService Stock { get; } = new();
        public FakeWallets Wallets { get; } = new();
        public FakeShipper Shipper { get; } = new();
        public RecordingQueue Queue { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public ReturnOptions Settings { get; set; } = new();

        public Fixture(ShopOrderStatus status = ShopOrderStatus.Delivered, int deliveredDaysAgo = 2)
        {
            var order = new Order
            {
                Id = 1, Number = "NM-1", CustomerId = Buyer, CurrencyCode = "USD", Total = 105m, PaymentMethod = "sandbox_redirect",
                ShopOrders =
                [
                    new ShopOrder
                    {
                        Id = ShopOrderId, OrderId = 1, Number = "NM-1-1", VendorId = Shop, ShopName = "Mugs Inc", Status = status, Subtotal = 100m,
                        DiscountAmount = 10m, ShippingFee = 5m, Total = 95m,
                        Lines =
                        [
                            new OrderLine { Id = Line1, ShopOrderId = ShopOrderId, ProductId = 1, Name = "Mug", VariantLabel = "Red", Quantity = 1, UnitPrice = 30m, LineTotal = 30m, TaxAmount = 3m },
                            new OrderLine { Id = Line2, ShopOrderId = ShopOrderId, ProductId = 2, Name = "Tea", Quantity = 1, UnitPrice = 40m, LineTotal = 40m }
                        ]
                    }
                ]
            };
            Orders.Orders.Add(order);
            Orders.History.Add(new OrderHistoryEntry(1, ShopOrderId, ShopOrderStatus.Shipped, ShopOrderStatus.Delivered, OrderActor.Shop, Seller, null, Now.AddDays(-deliveredDaysAgo)));
            Store.Orders = new FakeOrderHolder();
            Store.Orders.Orders.Add(order);
            Store.PaymentIds[1] = [7];
        }

        public ReturnService Create() => new(
            Store, Orders, new FakePrimaryCurrency(), Stock, Wallets, Shipper,
            new FakeCustomerIdentityStore(
                new Customer { Id = Buyer, Email = "ann@example.com", FirstName = "Ann <b>", Active = true },
                new Customer { Id = OtherBuyer, Email = "bob@example.com", Active = true }),
            Queue, Audit, new ReturnClock(), Microsoft.Extensions.Options.Options.Create(Settings), NullLogger<ReturnService>.Instance);

        public static ReturnCaller ShopCaller(int vendor = Shop) => new(OrderActor.Shop, Seller, vendor);
        public static ReturnCaller AdminCaller() => new(OrderActor.Admin, Admin, null);

        public async Task<ReturnRequest> RequestAsync(params (int Line, int Qty)[] lines)
        {
            var result = await Create().RequestAsync(Buyer, Command(lines), CancellationToken.None);
            return result.Value ?? throw new InvalidOperationException(result.ErrorCode ?? string.Join(",", result.Errors.Keys));
        }

        /// <summary>A return received but not refunded: the wallet is unreachable at the receipt, so it waits for an administrator.</summary>
        public async Task<ReturnRequest> ReceivedAsync()
        {
            var request = await RequestAsync((Line1, 1));
            await Create().ApproveAsync(ShopCaller(), request.Id, null, CancellationToken.None);
            Wallets.Throws = true;
            var received = (await Create().ReceiveAsync(ShopCaller(), request.Id, false, CancellationToken.None)).Value!;
            Wallets.Throws = false;
            return received;
        }
    }

    private static RequestReturnCommand Command((int Line, int Qty)[] lines, string? reason = "damaged", int shopOrder = ShopOrderId) =>
        new(shopOrder, reason, "It arrived broken", lines.Select(l => new ReturnLineRequest(l.Line, l.Qty)).ToList());

    private static RequestReturnCommand Command(string? reason) => Command([(Line1, 1)], reason);

    // ---- Rules ----

    [Theory]
    [InlineData(ReturnStatus.Requested, ReturnAction.Approve, OrderActor.Shop, ReturnStatus.Approved)]
    [InlineData(ReturnStatus.Requested, ReturnAction.Approve, OrderActor.Admin, ReturnStatus.Approved)]
    [InlineData(ReturnStatus.Requested, ReturnAction.Reject, OrderActor.Shop, ReturnStatus.Rejected)]
    [InlineData(ReturnStatus.Approved, ReturnAction.Receive, OrderActor.Shop, ReturnStatus.Received)]
    [InlineData(ReturnStatus.Received, ReturnAction.Refund, OrderActor.Admin, ReturnStatus.Refunded)]
    [InlineData(ReturnStatus.Requested, ReturnAction.Withdraw, OrderActor.Customer, ReturnStatus.Withdrawn)]
    public void AllowedStepsLeadWhereTheyShould(ReturnStatus from, ReturnAction action, OrderActor actor, ReturnStatus expected) =>
        Assert.Equal(expected, ReturnRules.Transition(from, action, actor));

    [Theory]
    [InlineData(ReturnStatus.Received, ReturnAction.Refund, OrderActor.Shop)]
    [InlineData(ReturnStatus.Approved, ReturnAction.Refund, OrderActor.Admin)]
    [InlineData(ReturnStatus.Requested, ReturnAction.Approve, OrderActor.Customer)]
    [InlineData(ReturnStatus.Approved, ReturnAction.Withdraw, OrderActor.Customer)]
    [InlineData(ReturnStatus.Requested, ReturnAction.Withdraw, OrderActor.Shop)]
    [InlineData(ReturnStatus.Rejected, ReturnAction.Approve, OrderActor.Admin)]
    [InlineData(ReturnStatus.Requested, ReturnAction.Receive, OrderActor.Shop)]
    [InlineData(ReturnStatus.Refunded, ReturnAction.Refund, OrderActor.Admin)]
    public void OtherStepsAreNotAllowed(ReturnStatus from, ReturnAction action, OrderActor actor) =>
        Assert.Null(ReturnRules.Transition(from, action, actor));

    [Theory]
    [InlineData(ShopOrderStatus.Delivered, 2, 14, true)]
    [InlineData(ShopOrderStatus.Delivered, 14, 14, true)]
    [InlineData(ShopOrderStatus.Completed, 2, 14, false)]
    [InlineData(ShopOrderStatus.Delivered, 15, 14, false)]
    [InlineData(ShopOrderStatus.Shipped, 1, 14, false)]
    [InlineData(ShopOrderStatus.Cancelled, 1, 14, false)]
    public void OnlyADeliveredOrderInsideTheWindowCanBeReturned(ShopOrderStatus status, int daysAgo, int window, bool expected) =>
        Assert.Equal(expected, ReturnRules.IsEligible(status, Now.AddDays(-daysAgo), Now, window));

    [Fact]
    public void AnOrderWithoutADeliveryDateCannotBeReturned() =>
        Assert.False(ReturnRules.IsEligible(ShopOrderStatus.Delivered, null, Now, 14));

    [Fact]
    public void TheRefundOfALineIsItsPriceMinusItsShareOfTheDiscountPlusItsTax()
    {
        var shop = new ShopOrder { Subtotal = 100m, DiscountAmount = 10m };
        var mug = new OrderLine { Quantity = 2, UnitPrice = 30m, TaxAmount = 6m };
        var tea = new OrderLine { Quantity = 1, UnitPrice = 40m };

        // 30 - 3 (30/100 of 10) + 3 (half of 6)
        Assert.Equal(30m, ReturnRules.RefundFor(mug, 1, shop, 2));
        Assert.Equal(60m, ReturnRules.RefundFor(mug, 2, shop, 2));
        Assert.Equal(36m, ReturnRules.RefundFor(tea, 1, shop, 2));
    }

    [Fact]
    public void TheRefundIsRoundedToTheCurrencyAndNeverNegative()
    {
        var shop = new ShopOrder { Subtotal = 30m, DiscountAmount = 10m };
        var line = new OrderLine { Quantity = 3, UnitPrice = 10m };

        Assert.Equal(6.67m, ReturnRules.RefundFor(line, 1, shop, 2));
        Assert.Equal(0m, ReturnRules.RefundFor(new OrderLine { Quantity = 1, UnitPrice = 0m }, 1, new ShopOrder { Subtotal = 0m, DiscountAmount = 5m }, 2));
    }

    [Theory]
    [InlineData(ReturnStatus.Requested, true)]
    [InlineData(ReturnStatus.Approved, true)]
    [InlineData(ReturnStatus.Received, true)]
    [InlineData(ReturnStatus.Refunded, true)]
    [InlineData(ReturnStatus.Rejected, false)]
    [InlineData(ReturnStatus.Withdrawn, false)]
    public void OnlyRejectedAndWithdrawnReturnsFreeTheirQuantity(ReturnStatus status, bool holds) =>
        Assert.Equal(holds, ReturnRules.HoldsQuantity(status));

    // ---- Request ----

    [Fact]
    public async Task ACustomerAsksForTheReturnOfTwoItemsAndTheRefundIsWorkedOutOnTheServer()
    {
        var f = new Fixture();

        var request = await f.RequestAsync((Line1, 1), (Line2, 1));

        Assert.Equal((ReturnStatus.Requested, Shop, "Mugs Inc", "damaged", "USD"), (request.Status, request.VendorId, request.ShopName, request.Reason, request.CurrencyCode));
        Assert.Equal(66m, request.RefundAmount);
        Assert.Equal([30m, 36m], request.Lines.Select(l => l.Amount));
        Assert.Equal(["Mug", "Tea"], request.Lines.Select(l => l.Name));
        Assert.StartsWith("RT", request.Number);
        Assert.Contains("return.requested", f.Audit.Entries.Select(e => e.Event));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("because")]
    public async Task AnUnknownReasonIsRefused(string? reason)
    {
        var result = await new Fixture().Create().RequestAsync(Buyer, Command(reason), CancellationToken.None);

        Assert.Contains("reason", result.Errors.Keys);
    }

    [Fact]
    public async Task ARequestNeedsItemsEachOnceWithAQuantityAndAShortNote()
    {
        var f = new Fixture();

        Assert.Contains("lines", (await f.Create().RequestAsync(Buyer, new RequestReturnCommand(ShopOrderId, "damaged", null, []), CancellationToken.None)).Errors.Keys);
        Assert.Contains("lines", (await f.Create().RequestAsync(Buyer, Command([(Line1, 1), (Line1, 1)]), CancellationToken.None)).Errors.Keys);
        Assert.Contains("lines", (await f.Create().RequestAsync(Buyer, Command([(Line1, 0)]), CancellationToken.None)).Errors.Keys);
        Assert.Contains("note", (await f.Create().RequestAsync(Buyer,
            new RequestReturnCommand(ShopOrderId, "damaged", new string('x', 501), [new ReturnLineRequest(Line1, 1)]), CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task AnItemThatIsNotPartOfTheOrderIsRefused()
    {
        var result = await new Fixture().Create().RequestAsync(Buyer, Command([(999, 1)]), CancellationToken.None);

        Assert.Contains("lines", result.Errors.Keys);
    }

    [Fact]
    public async Task SomeoneElsesOrderAndAnUnknownOrderAreNotFound()
    {
        var f = new Fixture();

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().RequestAsync(OtherBuyer, Command([(Line1, 1)]), CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().RequestAsync(Buyer, Command([(Line1, 1)], shopOrder: 999), CancellationToken.None)).ErrorCode);
    }

    [Theory]
    [InlineData(ShopOrderStatus.Pending)]
    [InlineData(ShopOrderStatus.Shipped)]
    [InlineData(ShopOrderStatus.Cancelled)]
    public async Task AnOrderThatWasNotDeliveredCannotBeReturned(ShopOrderStatus status)
    {
        var result = await new Fixture(status).Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None);

        Assert.Equal(ReturnErrors.NotEligible, result.ErrorCode);
    }

    [Fact]
    public async Task ADeliveredOrderCanBeReturnedInsideTheWindowButNotAfterItNorOnceReceiptIsConfirmed()
    {
        Assert.True((await new Fixture(ShopOrderStatus.Delivered, 10).Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None)).Succeeded);

        var late = await new Fixture(ShopOrderStatus.Delivered, 15).Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None);
        Assert.Equal(ReturnErrors.NotEligible, late.ErrorCode);

        var longer = new Fixture(ShopOrderStatus.Delivered, 15) { Settings = new ReturnOptions { WindowDays = 30 } };
        Assert.True((await longer.Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None)).Succeeded);

        var received = await new Fixture(ShopOrderStatus.Completed, 1).Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None);
        Assert.Equal(ReturnErrors.NotEligible, received.ErrorCode);
    }

    [Fact]
    public async Task AnItemIsReturnedWholeNeverPartOfItsUnits()
    {
        var f = new Fixture();
        f.Orders.Orders[0].ShopOrders[0].Lines[0].Quantity = 3;

        Assert.Contains("lines", (await f.Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None)).Errors.Keys);
        Assert.Contains("lines", (await f.Create().RequestAsync(Buyer, Command([(Line1, 4)]), CancellationToken.None)).Errors.Keys);
        Assert.True((await f.Create().RequestAsync(Buyer, Command([(Line1, 3)]), CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task AnItemIsReturnedOnceAndARejectedReturnFreesIt()
    {
        var f = new Fixture();

        var first = await f.RequestAsync((Line1, 1));
        Assert.Equal(ReturnErrors.QuantityExceeded, (await f.Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None)).ErrorCode);

        await f.Create().RejectAsync(Fixture.ShopCaller(), first.Id, "Not eligible", CancellationToken.None);
        Assert.True((await f.Create().RequestAsync(Buyer, Command([(Line1, 1)]), CancellationToken.None)).Succeeded);
    }

    // ---- Customer views ----

    [Fact]
    public async Task ACustomerSeesOnlyTheirOwnReturns()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        Assert.Single((await f.Create().GetMineAsync(Buyer, 1, 10, CancellationToken.None)).Items);
        Assert.Empty((await f.Create().GetMineAsync(OtherBuyer, 1, 10, CancellationToken.None)).Items);
        Assert.True((await f.Create().GetMineAsync(Buyer, request.Id, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetMineAsync(OtherBuyer, request.Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ACustomerCanWithdrawARequestOnlyWhileItWaits()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().WithdrawAsync(OtherBuyer, request.Id, CancellationToken.None)).ErrorCode);
        Assert.Equal(ReturnStatus.Withdrawn, (await f.Create().WithdrawAsync(Buyer, request.Id, CancellationToken.None)).Value!.Status);
        Assert.Equal(ReturnErrors.InvalidTransition, (await f.Create().WithdrawAsync(Buyer, request.Id, CancellationToken.None)).ErrorCode);

        var second = await f.RequestAsync((Line1, 1));
        await f.Create().ApproveAsync(Fixture.ShopCaller(), second.Id, null, CancellationToken.None);
        Assert.Equal(ReturnErrors.InvalidTransition, (await f.Create().WithdrawAsync(Buyer, second.Id, CancellationToken.None)).ErrorCode);
    }

    // ---- Shop and admin ----

    [Fact]
    public async Task AShopSeesAndActsOnItsOwnReturnsOnly()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        Assert.Single((await f.Create().GetAsync(Fixture.ShopCaller(), null, 1, 20, CancellationToken.None)).Items);
        Assert.Empty((await f.Create().GetAsync(Fixture.ShopCaller(OtherShop), null, 1, 20, CancellationToken.None)).Items);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetAsync(Fixture.ShopCaller(OtherShop), request.Id, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().ApproveAsync(Fixture.ShopCaller(OtherShop), request.Id, null, CancellationToken.None)).ErrorCode);
        Assert.Equal(ReturnStatus.Requested, f.Store.Raw(request.Id).Status);
    }

    [Fact]
    public async Task AnAdministratorSeesEveryReturnAndCanDecideToo()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        Assert.Single((await f.Create().GetAsync(Fixture.AdminCaller(), null, 1, 20, CancellationToken.None)).Items);
        Assert.Equal(ReturnStatus.Approved, (await f.Create().ApproveAsync(Fixture.AdminCaller(), request.Id, null, CancellationToken.None)).Value!.Status);
    }

    [Fact]
    public async Task TheListCanBeFilteredByStatus()
    {
        var f = new Fixture();
        await f.RequestAsync((Line1, 1));
        var second = await f.RequestAsync((Line2, 1));
        await f.Create().ApproveAsync(Fixture.ShopCaller(), second.Id, null, CancellationToken.None);

        var approved = await f.Create().GetAsync(Fixture.AdminCaller(), ReturnStatus.Approved, 1, 20, CancellationToken.None);

        Assert.Equal(second.Id, Assert.Single(approved.Items).Id);
    }

    [Fact]
    public async Task ApprovingTellsTheCustomerWithTheShopsNoteEncodedAndCannotBeDoneTwice()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        var approved = await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, "  Send it to <our depot>  ", CancellationToken.None);

        Assert.Equal((ReturnStatus.Approved, "Send it to <our depot>"), (approved.Value!.Status, approved.Value.ResolutionNote));
        var (kind, message) = Assert.Single(f.Queue.Queued);
        Assert.Equal((EmailKinds.ReturnApproved, "ann@example.com"), (kind, message.ToAddress));
        Assert.Contains("Send it to &lt;our depot&gt;", message.HtmlBody);
        Assert.Contains("Hello Ann &lt;b&gt;,", message.HtmlBody);
        Assert.Equal(ReturnErrors.InvalidTransition, (await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None)).ErrorCode);
        Assert.Single(f.Queue.Queued);
    }

    [Fact]
    public async Task RejectingNeedsAReasonAndTellsTheCustomer()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        Assert.Contains("note", (await f.Create().RejectAsync(Fixture.ShopCaller(), request.Id, " ", CancellationToken.None)).Errors.Keys);
        Assert.Equal(ReturnStatus.Requested, f.Store.Raw(request.Id).Status);

        var rejected = await f.Create().RejectAsync(Fixture.ShopCaller(), request.Id, "Used item", CancellationToken.None);

        Assert.Equal((ReturnStatus.Rejected, "Used item"), (rejected.Value!.Status, rejected.Value.ResolutionNote));
        Assert.Equal(EmailKinds.ReturnRejected, f.Queue.Queued.Single().Kind);
    }

    [Fact]
    public async Task ReceivingNeedsAnApprovedReturn()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        Assert.Equal(ReturnErrors.InvalidTransition, (await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, true, CancellationToken.None)).ErrorCode);
        Assert.Empty(f.Stock.Returns);
    }

    [Fact]
    public async Task ReceivingWithRestockPutsTheReturnedUnitsBackOnSale()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1), (Line2, 1));
        await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None);

        var received = await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, true, CancellationToken.None);

        Assert.Equal((ReturnStatus.Refunded, true), (received.Value!.Status, received.Value.Restocked));
        Assert.Equal([(request.Number, 1, (int?)null, 1), (request.Number, 2, (int?)null, 1)], f.Stock.Returns.Select(r => (r.Reference, r.ProductId, r.CombinationId, r.Quantity)));
    }

    [Fact]
    public async Task ReceivingWithoutRestockLeavesTheStockAlone()
    {
        var f = new Fixture();
        var received = await f.ReceivedAsync();

        Assert.False(received.Restocked);
        Assert.Empty(f.Stock.Returns);
    }

    [Fact]
    public async Task AStockReturnThatFailsLeavesTheReceiptDoneAndIsAudited()
    {
        var f = new Fixture();
        f.Stock.FailReturns = true;
        var request = await f.RequestAsync((Line1, 1));
        await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None);

        var received = await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, true, CancellationToken.None);

        Assert.Equal(ReturnStatus.Refunded, received.Value!.Status);
        Assert.Contains("return.restock_failed", f.Audit.Entries.Select(e => e.Event));
    }

    [Fact]
    public async Task TheStockOfAReceivedReturnGoesBackOnceEvenIfReceivedTwice()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));
        await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None);

        await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, true, CancellationToken.None);
        var again = await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, true, CancellationToken.None);

        Assert.Equal(ReturnErrors.InvalidTransition, again.ErrorCode);
        Assert.Single(f.Stock.Returns);
    }

    // ---- Refund (to the wallet) ----

    [Fact]
    public async Task ReceivingTheGoodsPutsTheRefundInTheWalletAtOnceAndTellsTheCustomer()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));
        await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None);
        f.Queue.Queued.Clear();

        var refunded = (await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, false, CancellationToken.None)).Value!;

        Assert.Equal(ReturnStatus.Refunded, refunded.Status);
        Assert.Equal((Buyer, 30m, $"return-{request.Id}"), Assert.Single(f.Wallets.Credits));
        Assert.Contains("return.refunded", f.Audit.Entries.Select(e => e.Event));
        var (kind, message) = Assert.Single(f.Queue.Queued);
        Assert.Equal(EmailKinds.ReturnRefunded, kind);
        Assert.Contains("30 USD", message.HtmlBody);
        Assert.Contains("wallet", message.HtmlBody);
    }

    [Fact]
    public async Task AWalletThatCannotBeReachedLeavesTheReturnReceivedForAnAdministrator()
    {
        var f = new Fixture();

        var received = await f.ReceivedAsync();

        Assert.Equal(ReturnStatus.Received, received.Status);
        Assert.Empty(f.Wallets.Credits);
        var refunded = await f.Create().RefundAsync(Fixture.AdminCaller(), received.Id, false, CancellationToken.None);
        Assert.Equal(ReturnStatus.Refunded, refunded.Value!.Status);
        Assert.Equal(30m, Assert.Single(f.Wallets.Credits).Amount);
    }

    [Fact]
    public async Task OnlyAnAdministratorCanRefundAndOnlyAReceivedReturn()
    {
        var f = new Fixture();
        var received = await f.ReceivedAsync();
        var waiting = await f.RequestAsync((Line2, 1));

        Assert.Equal(ReturnErrors.InvalidTransition, (await f.Create().RefundAsync(Fixture.ShopCaller(), received.Id, false, CancellationToken.None)).ErrorCode);
        Assert.Equal(ReturnErrors.InvalidTransition, (await f.Create().RefundAsync(Fixture.AdminCaller(), waiting.Id, false, CancellationToken.None)).ErrorCode);
        Assert.Empty(f.Wallets.Credits);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().RefundAsync(Fixture.AdminCaller(), 999, false, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task TwoClicksOnRefundCreditTheWalletOnlyOnce()
    {
        var f = new Fixture();
        var received = await f.ReceivedAsync();

        await f.Create().RefundAsync(Fixture.AdminCaller(), received.Id, false, CancellationToken.None);
        var second = await f.Create().RefundAsync(Fixture.AdminCaller(), received.Id, false, CancellationToken.None);

        Assert.Equal(ReturnErrors.InvalidTransition, second.ErrorCode);
        Assert.Single(f.Wallets.Credits);
    }

    [Fact]
    public async Task ARefundMadeByHandCreditsNothing()
    {
        var f = new Fixture();
        var received = await f.ReceivedAsync();

        var refunded = await f.Create().RefundAsync(Fixture.AdminCaller(), received.Id, true, CancellationToken.None);

        Assert.Equal(ReturnStatus.Refunded, refunded.Value!.Status);
        Assert.Empty(f.Wallets.Credits);
    }

    // ---- The shop order after its returns ----

    private static async Task RefundAsync(Fixture f, ReturnRequest request)
    {
        await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None);
        var received = await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, false, CancellationToken.None);
        Assert.Equal(ReturnStatus.Refunded, received.Value!.Status);
    }

    [Fact]
    public async Task APartialReturnLeavesTheShopOrderAsItIsAndTheLastRefundMakesItReturned()
    {
        var f = new Fixture();
        var shopOrder = f.Orders.Orders[0].ShopOrders[0];

        await RefundAsync(f, await f.RequestAsync((Line1, 1)));
        Assert.Equal(ShopOrderStatus.Delivered, shopOrder.Status);

        await RefundAsync(f, await f.RequestAsync((Line2, 1)));
        Assert.Equal(ShopOrderStatus.Returned, shopOrder.Status);
        var change = f.Orders.History[^1];
        Assert.Equal((ShopOrderStatus.Returned, OrderActor.System), (change.ToStatus, change.Actor));
    }

    [Fact]
    public async Task ReturningEverythingAtOnceEndsReturned()
    {
        var f = new Fixture();

        await RefundAsync(f, await f.RequestAsync((Line1, 1), (Line2, 1)));

        Assert.Equal(ShopOrderStatus.Returned, f.Orders.Orders[0].ShopOrders[0].Status);
    }

    // ---- The shipping the customer paid ----

    [Theory]
    [InlineData(ReturnReasons.Damaged, ReturnPayers.Shop)]
    [InlineData(ReturnReasons.ChangedMind, ReturnPayers.Platform)]
    public async Task AShopOrderReturnedWholeGivesTheShippingBackWhateverTheReason(string reason, string payer)
    {
        var f = new Fixture();
        var request = (await f.Create().RequestAsync(Buyer, Because(reason, (Line1, 1), (Line2, 1)), CancellationToken.None)).Value!;

        await RefundAsync(f, request);

        var refunded = (await f.Store.GetAsync(request.Id, CancellationToken.None))!;
        Assert.Equal((5m, payer), (refunded.ShippingRefund, refunded.ShippingRefundPaidBy));
        Assert.Equal(request.RefundAmount + 5m, Assert.Single(f.Wallets.Credits).Amount);
        Assert.Equal(payer == ReturnPayers.Shop ? 5m : 0m, ReturnRules.ShopShipping(refunded));
    }

    [Fact]
    public async Task OnlyTheReturnThatCompletesTheShopOrderGivesTheShippingBack()
    {
        var f = new Fixture();
        var first = await f.RequestAsync((Line1, 1));
        await RefundAsync(f, first);
        var second = await f.RequestAsync((Line2, 1));
        await RefundAsync(f, second);

        Assert.Equal(0m, (await f.Store.GetAsync(first.Id, CancellationToken.None))!.ShippingRefund);
        Assert.Equal(5m, (await f.Store.GetAsync(second.Id, CancellationToken.None))!.ShippingRefund);
        Assert.Equal([first.RefundAmount, second.RefundAmount + 5m], f.Wallets.Credits.Select(c => c.Amount));
    }

    [Fact]
    public async Task APartReturnedGivesNoShippingBack()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));

        await RefundAsync(f, request);

        Assert.Equal(0m, (await f.Store.GetAsync(request.Id, CancellationToken.None))!.ShippingRefund);
        Assert.Equal(request.RefundAmount, Assert.Single(f.Wallets.Credits).Amount);
    }

    [Fact]
    public void TheShippingGivenBackIsPartOfThePayOutAndThePayerIsTheShopOnlyForFaultyGoods()
    {
        var request = new ReturnRequest { RefundAmount = 30m, ShippingRefund = 5m, ReturnShippingFee = 4m, ReturnShippingPaidBy = ReturnPayers.Customer };

        Assert.Equal(31m, ReturnRules.PayOut(request));
        Assert.Equal(ReturnPayers.Platform, ReturnRules.ShippingRefundPayer(ReturnReasons.ChangedMind));
        Assert.Equal(ReturnPayers.Shop, ReturnRules.ShippingRefundPayer(ReturnReasons.WrongItem));
    }

    [Fact]
    public void AShopOrderIsReturnedWholeOnlyWhenEveryUnitWasRefunded()
    {
        var lines = new[] { new OrderLine { Id = 1, Quantity = 2 }, new OrderLine { Id = 2, Quantity = 1 } };

        Assert.False(ReturnRules.IsReturnedWhole(lines, new Dictionary<int, int> { [1] = 2 }));
        Assert.True(ReturnRules.IsReturnedWhole(lines, new Dictionary<int, int> { [1] = 2, [2] = 1 }));
    }

    // ---- The parcel back (GHN) ----

    private static RequestReturnCommand Because(string reason, params (int Line, int Qty)[] lines) =>
        new(ShopOrderId, reason, null, lines.Select(l => new ReturnLineRequest(l.Line, l.Qty)).ToList());

    [Fact]
    public async Task ApprovingMakesTheParcelBackAndTheCustomerPaysItWhenTheyNoLongerNeedTheGoods()
    {
        var f = new Fixture();
        f.Shipper.Next = new ReturnShipment("LBACK1", 5m, null);
        var request = (await f.Create().RequestAsync(Buyer, Because(ReturnReasons.ChangedMind, (Line1, 1)), CancellationToken.None)).Value!;

        var approved = (await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None)).Value!;

        Assert.Equal(("LBACK1", 5m, ReturnPayers.Customer), (approved.ReturnTrackingNumber, approved.ReturnShippingFee, approved.ReturnShippingPaidBy));
        Assert.Equal(request.RefundAmount - 5m, ReturnRules.PayOut(approved));
        Assert.Contains("LBACK1", Assert.Single(f.Queue.Queued).Message.HtmlBody);

        var refunded = (await f.Create().ReceiveAsync(Fixture.ShopCaller(), request.Id, false, CancellationToken.None)).Value!;
        Assert.Equal(ReturnStatus.Refunded, refunded.Status);
        Assert.Equal(request.RefundAmount - 5m, Assert.Single(f.Wallets.Credits).Amount);
    }

    [Fact]
    public async Task TheShopPaysTheParcelBackWhenTheGoodsWereAtFault()
    {
        var f = new Fixture();
        f.Shipper.Next = new ReturnShipment("LBACK1", 5m, null);
        var request = await f.RequestAsync((Line1, 1));

        var approved = (await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None)).Value!;

        Assert.Equal(ReturnPayers.Shop, approved.ReturnShippingPaidBy);
        Assert.Equal(request.RefundAmount, ReturnRules.PayOut(approved));
        Assert.Equal(5m, ReturnRules.ShopShipping(approved));
    }

    [Fact]
    public async Task AParcelThatCannotBeMadeLeavesTheReturnApprovedAndCanBeMadeAgainOnce()
    {
        var f = new Fixture();
        f.Shipper.Next = new ReturnShipment(null, 0m, "GHN: bad address");
        var request = await f.RequestAsync((Line1, 1));

        var approved = (await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None)).Value!;
        Assert.Equal((ReturnStatus.Approved, "GHN: bad address", (string?)null), (approved.Status, approved.ReturnShipmentError, approved.ReturnTrackingNumber));

        var failedAgain = await f.Create().ShipBackAsync(Fixture.ShopCaller(), request.Id, CancellationToken.None);
        Assert.Contains("shipment", failedAgain.Errors.Keys);

        f.Shipper.Next = new ReturnShipment("LBACK2", 4m, null);
        var made = (await f.Create().ShipBackAsync(Fixture.ShopCaller(), request.Id, CancellationToken.None)).Value!;
        Assert.Equal(("LBACK2", (string?)null), (made.ReturnTrackingNumber, made.ReturnShipmentError));

        Assert.Equal(ReturnErrors.ShipmentNotNeeded, (await f.Create().ShipBackAsync(Fixture.ShopCaller(), request.Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task WithoutACarrierTheCustomerSendsTheGoodsAndACarrierThatFailsDoesNotUndoTheApproval()
    {
        var f = new Fixture();
        var request = await f.RequestAsync((Line1, 1));
        var approved = (await f.Create().ApproveAsync(Fixture.ShopCaller(), request.Id, null, CancellationToken.None)).Value!;
        Assert.Null(approved.ReturnTrackingNumber);
        Assert.Equal(ReturnErrors.NoCarrier, (await f.Create().ShipBackAsync(Fixture.ShopCaller(), request.Id, CancellationToken.None)).ErrorCode);

        var other = new Fixture();
        other.Shipper.Throws = true;
        var second = await other.RequestAsync((Line2, 1));
        var stillApproved = await other.Create().ApproveAsync(Fixture.ShopCaller(), second.Id, null, CancellationToken.None);
        Assert.Equal(ReturnStatus.Approved, stillApproved.Value!.Status);
        Assert.NotNull(stillApproved.Value.ReturnShipmentError);
    }

    [Fact]
    public void TheReturnShippingNeverMakesThePayOutNegative()
    {
        var request = new ReturnRequest { RefundAmount = 3m, ReturnShippingFee = 5m, ReturnShippingPaidBy = ReturnPayers.Customer };
        Assert.Equal(0m, ReturnRules.PayOut(request));
        Assert.Equal(ReturnPayers.Customer, ReturnRules.ShippingPayer(ReturnReasons.ChangedMind));
        Assert.All(new[] { ReturnReasons.Damaged, ReturnReasons.WrongItem, ReturnReasons.NotAsDescribed, ReturnReasons.Other },
            reason => Assert.Equal(ReturnPayers.Shop, ReturnRules.ShippingPayer(reason)));
    }
}
