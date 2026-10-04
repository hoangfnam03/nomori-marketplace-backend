using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Orders;

namespace Nomori.Marketplace.Services.Tests;

public sealed class OrderTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Buyer = 100;
    private const int OtherBuyer = 101;
    private const int Seller = 10;
    private const int Admin = 1;

    private sealed class FakeOrderStore : IOrderStore
    {
        private int nextOrderId = 1;
        private int nextShopOrderId = 1;
        private int nextHistoryId = 1;

        public List<Order> Orders { get; } = [];
        public List<OrderHistoryEntry> History { get; } = [];

        /// <summary>Runs just before a status change is checked, so a test can play another request that got there first.</summary>
        public Action<ShopOrder>? BeforeChange { get; set; }

        private ShopOrder? Find(int shopOrderId) => Orders.SelectMany(o => o.ShopOrders).FirstOrDefault(s => s.Id == shopOrderId);

        public Task<(Order Order, bool Created)> InsertAsync(Order order, CancellationToken cancellationToken)
        {
            var existing = Orders.FirstOrDefault(o => o.PlacementKey == order.PlacementKey);
            if (existing is not null) return Task.FromResult((existing, false));

            order.Id = nextOrderId++;
            order.Number = OrderRules.NumberFor(order.CreatedOnUtc, order.Id);
            var position = 1;
            foreach (var shop in order.ShopOrders)
            {
                shop.Id = nextShopOrderId++;
                shop.OrderId = order.Id;
                shop.Number = OrderRules.ShopNumberFor(order.Number, position++);
                shop.ItemCount = shop.Lines.Sum(l => l.Quantity);
                foreach (var line in shop.Lines) line.ShopOrderId = shop.Id;
                History.Add(new OrderHistoryEntry(nextHistoryId++, shop.Id, null, ShopOrderStatus.Pending, OrderActor.Customer, order.CustomerId, null, order.CreatedOnUtc));
            }
            Orders.Add(order);
            return Task.FromResult((order, true));
        }

        public Task<Order?> GetOrderAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Orders.FirstOrDefault(o => o.Id == id));

        public Task<ShopOrder?> GetShopOrderAsync(int id, CancellationToken cancellationToken)
        {
            var shop = Find(id);
            if (shop is not null) shop.Order = Orders.Single(o => o.Id == shop.OrderId);
            return Task.FromResult(shop);
        }

        public Task<PagedResult<Order>> GetOrdersAsync(OrderListQuery query, CancellationToken cancellationToken)
        {
            var all = Orders.Where(o => (query.CustomerId is null || o.CustomerId == query.CustomerId)
                && (query.VendorId is null || o.ShopOrders.Any(s => s.VendorId == query.VendorId))
                && (query.Status is null || o.ShopOrders.Any(s => s.Status == query.Status))).OrderByDescending(o => o.Id).ToList();
            return Task.FromResult(new PagedResult<Order>(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
        }

        public Task<PagedResult<ShopOrder>> GetShopOrdersAsync(OrderListQuery query, CancellationToken cancellationToken)
        {
            var all = Orders.SelectMany(o => o.ShopOrders.Select(s => { s.Order = o; return s; }))
                .Where(s => (query.VendorId is null || s.VendorId == query.VendorId) && (query.Status is null || s.Status == query.Status)
                    && (query.Search is null || s.Number.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
                        || s.Order!.RecipientName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(s => s.Id).ToList();
            return Task.FromResult(new PagedResult<ShopOrder>(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
        }

        public Task<IReadOnlyDictionary<ShopOrderStatus, int>> CountByStatusAsync(int vendorId, CancellationToken cancellationToken)
        {
            var shops = Orders.SelectMany(o => o.ShopOrders).Where(s => s.VendorId == vendorId).ToList();
            return Task.FromResult<IReadOnlyDictionary<ShopOrderStatus, int>>(Enum.GetValues<ShopOrderStatus>().ToDictionary(s => s, s => shops.Count(x => x.Status == s)));
        }

        public Task<bool> TryTransitionAsync(ShopOrderTransition transition, CancellationToken cancellationToken)
        {
            var shop = Find(transition.ShopOrderId)!;
            BeforeChange?.Invoke(shop);
            if (shop.Status != transition.Expected) return Task.FromResult(false);
            shop.Status = transition.Target;
            shop.Carrier = transition.Carrier ?? shop.Carrier;
            shop.TrackingNumber = transition.TrackingNumber ?? shop.TrackingNumber;
            shop.CancelReason = transition.CancelReason ?? shop.CancelReason;
            History.Add(new OrderHistoryEntry(nextHistoryId++, shop.Id, transition.Expected, transition.Target, transition.Actor, transition.ActorCustomerId, transition.Note, transition.NowUtc));
            return Task.FromResult(true);
        }

        public Task<bool> TryUpdateTrackingAsync(int shopOrderId, string carrier, string trackingNumber, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var shop = Find(shopOrderId)!;
            if (shop.Status != ShopOrderStatus.Shipped) return Task.FromResult(false);
            (shop.Carrier, shop.TrackingNumber) = (carrier, trackingNumber);
            History.Add(new OrderHistoryEntry(nextHistoryId++, shop.Id, ShopOrderStatus.Shipped, ShopOrderStatus.Shipped, OrderActor.Shop, actorCustomerId, $"{carrier} {trackingNumber}", nowUtc));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<OrderHistoryEntry>> GetHistoryAsync(IReadOnlyCollection<int> shopOrderIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderHistoryEntry>>(History.Where(h => shopOrderIds.Contains(h.ShopOrderId)).ToList());
    }

    private sealed class Fixture
    {
        public FakeOrderStore Store { get; } = new();
        public FakeVendorStore Vendors { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Mugs Inc", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Tea Co", Active = true });
        }

        public OrderService Create(int decimals = 2) => new(Store, Vendors, new FakePrimaryCurrency(decimals), Audit, new TestClock());

        /// <summary>An order with one shop order per given shop, owned by the buyer.</summary>
        public async Task<Order> PlaceAsync(string key = "k1", int customer = Buyer, params int[] shops)
        {
            var result = await Create().CreateAsync(Command(key, customer, shops.Length == 0 ? [Shop, OtherShop] : shops), CancellationToken.None);
            return result.Value!;
        }

    }

    /// <summary>Puts a shop order of the order in a status, as if earlier steps had happened.</summary>
    private static ShopOrder Move(Order order, int vendor, ShopOrderStatus status)
    {
        var shop = order.ShopOrders.Single(s => s.VendorId == vendor);
        shop.Status = status;
        return shop;
    }

    private static NewOrderCommand Command(string? key = "k1", int customer = Buyer, int[]? shops = null, string? method = "cod") => new(
        customer, key, method, "Leave at the door", Recipient(),
        (shops ?? [Shop, OtherShop]).Select(v => new NewShopOrder(v, "Standard", 3, 4.5m, [Line(1, 2, 10m), Line(2, 1, 2.5m)])).ToList());

    private static NewOrderRecipient Recipient() => new("Ann Lee", "+1 555 0100", "1 Main St", null, "Springfield", "IL", "62701", "us");

    private static NewOrderLine Line(int productId, int quantity, decimal price) =>
        new(productId, null, "Mug " + productId, "Red / S", "SKU-" + productId, 7, quantity, price);

    // ---- Pure rules ----

    [Theory]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Confirm, OrderActor.Shop, ShopOrderStatus.Confirmed)]
    [InlineData(ShopOrderStatus.Confirmed, OrderAction.Ship, OrderActor.Shop, ShopOrderStatus.Shipped)]
    [InlineData(ShopOrderStatus.Shipped, OrderAction.Deliver, OrderActor.Shop, ShopOrderStatus.Delivered)]
    [InlineData(ShopOrderStatus.Shipped, OrderAction.Deliver, OrderActor.Customer, ShopOrderStatus.Delivered)]
    [InlineData(ShopOrderStatus.Delivered, OrderAction.Complete, OrderActor.System, ShopOrderStatus.Completed)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Cancel, OrderActor.Shop, ShopOrderStatus.Cancelled)]
    [InlineData(ShopOrderStatus.Confirmed, OrderAction.Cancel, OrderActor.Shop, ShopOrderStatus.Cancelled)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Cancel, OrderActor.Customer, ShopOrderStatus.Cancelled)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Cancel, OrderActor.System, ShopOrderStatus.Cancelled)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Cancel, OrderActor.Admin, ShopOrderStatus.Cancelled)]
    [InlineData(ShopOrderStatus.Shipped, OrderAction.Cancel, OrderActor.Admin, ShopOrderStatus.Cancelled)]
    [InlineData(ShopOrderStatus.Delivered, OrderAction.Cancel, OrderActor.Admin, ShopOrderStatus.Cancelled)]
    public void LegalTransitionsLeadWhereTheLifecycleSays(ShopOrderStatus from, OrderAction action, OrderActor actor, ShopOrderStatus expected) =>
        Assert.Equal(expected, OrderRules.Transition(from, action, actor));

    [Theory]
    [InlineData(ShopOrderStatus.Confirmed, OrderAction.Cancel, OrderActor.Customer)]
    [InlineData(ShopOrderStatus.Shipped, OrderAction.Cancel, OrderActor.Shop)]
    [InlineData(ShopOrderStatus.Delivered, OrderAction.Cancel, OrderActor.Shop)]
    [InlineData(ShopOrderStatus.Confirmed, OrderAction.Cancel, OrderActor.System)]
    [InlineData(ShopOrderStatus.Completed, OrderAction.Cancel, OrderActor.Admin)]
    [InlineData(ShopOrderStatus.Cancelled, OrderAction.Cancel, OrderActor.Admin)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Confirm, OrderActor.Customer)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Confirm, OrderActor.Admin)]
    [InlineData(ShopOrderStatus.Confirmed, OrderAction.Confirm, OrderActor.Shop)]
    [InlineData(ShopOrderStatus.Pending, OrderAction.Ship, OrderActor.Shop)]
    [InlineData(ShopOrderStatus.Confirmed, OrderAction.Deliver, OrderActor.Shop)]
    [InlineData(ShopOrderStatus.Shipped, OrderAction.Deliver, OrderActor.Admin)]
    [InlineData(ShopOrderStatus.Delivered, OrderAction.Complete, OrderActor.Shop)]
    [InlineData(ShopOrderStatus.Delivered, OrderAction.Complete, OrderActor.Admin)]
    [InlineData(ShopOrderStatus.Shipped, OrderAction.Complete, OrderActor.System)]
    public void IllegalTransitionsHaveNoTarget(ShopOrderStatus from, OrderAction action, OrderActor actor) =>
        Assert.Null(OrderRules.Transition(from, action, actor));

    [Fact]
    public void TheOrderStatusIsDerivedFromItsShopOrders()
    {
        Assert.Equal(OverallOrderStatus.Processing, OrderRules.Overall([ShopOrderStatus.Completed, ShopOrderStatus.Shipped]));
        Assert.Equal(OverallOrderStatus.Processing, OrderRules.Overall([ShopOrderStatus.Cancelled, ShopOrderStatus.Pending]));
        Assert.Equal(OverallOrderStatus.Completed, OrderRules.Overall([ShopOrderStatus.Completed, ShopOrderStatus.Cancelled]));
        Assert.Equal(OverallOrderStatus.Cancelled, OrderRules.Overall([ShopOrderStatus.Cancelled, ShopOrderStatus.Cancelled]));
    }

    [Fact]
    public void NumbersAndWireNamesAreStable()
    {
        Assert.Equal("NM260926-0042", OrderRules.NumberFor(new DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc), 42));
        Assert.Equal("NM260926-0042-2", OrderRules.ShopNumberFor("NM260926-0042", 2));
        foreach (var status in Enum.GetValues<ShopOrderStatus>())
        {
            Assert.True(OrderRules.TryParseWire(OrderRules.ToWire(status), out var parsed));
            Assert.Equal(status, parsed);
        }
        Assert.False(OrderRules.TryParseWire("lost", out _));
        Assert.Equal(OrderActor.Admin, OrderRules.ParseActor(OrderRules.ToWire(OrderActor.Admin)));
    }

    [Fact]
    public void LineTotalsRoundToTheCurrency() =>
        Assert.Equal(3.35m, OrderRules.LineTotal(1.115m, 3, 2));

    // ---- Create ----

    [Fact]
    public async Task TotalsAreComputedByTheServiceAndSnapshotsAreKept()
    {
        var f = new Fixture();

        var order = await f.PlaceAsync();

        Assert.Equal(2, order.ShopOrders.Count);
        var first = order.ShopOrders[0];
        Assert.Equal((22.5m, 4.5m, 27m), (first.Subtotal, first.ShippingFee, first.Total));
        Assert.Equal((45m, 9m, 54m, "USD"), (order.Subtotal, order.ShippingTotal, order.Total, order.CurrencyCode));
        Assert.Equal(("Mugs Inc", "Standard", ShopOrderStatus.Pending), (first.ShopName, first.ShippingMethodName, first.Status));
        Assert.Equal((20m, "Mug 1", "Red / S", 7), (first.Lines[0].LineTotal, first.Lines[0].Name, first.Lines[0].VariantLabel, first.Lines[0].PictureId));
        Assert.Equal(("Ann Lee", "US", "cod"), (order.RecipientName, order.CountryCode, order.PaymentMethod));
        Assert.EndsWith("-1", first.Number);
        Assert.EndsWith("-2", order.ShopOrders[1].Number);
        Assert.Contains("order.created", f.Audit.Events);
    }

    [Fact]
    public async Task EveryShopOrderStartsPendingWithAHistoryRow()
    {
        var f = new Fixture();

        var order = await f.PlaceAsync();

        Assert.Equal(2, f.Store.History.Count);
        Assert.All(f.Store.History, h => Assert.Equal((null, ShopOrderStatus.Pending, OrderActor.Customer, Buyer), (h.FromStatus, h.ToStatus, h.Actor, h.ActorCustomerId)));
        Assert.Equal(order.ShopOrders.Select(s => s.Id).Order(), f.Store.History.Select(h => h.ShopOrderId).Order());
    }

    [Fact]
    public async Task TheSamePlacementKeyReturnsTheSameOrder()
    {
        var f = new Fixture();

        var first = await f.PlaceAsync();
        var again = await f.Create().CreateAsync(Command(), CancellationToken.None);

        Assert.True(again.Succeeded);
        Assert.Equal(first.Id, again.Value!.Id);
        Assert.Single(f.Store.Orders);
        Assert.Single(f.Audit.Events, e => e == "order.created");
    }

    [Fact]
    public async Task TheSameKeyWithAnotherRequestIsAConflict()
    {
        var f = new Fixture();
        await f.PlaceAsync();

        Assert.Equal(OrderErrors.PlacementConflict, (await f.Create().CreateAsync(Command(customer: OtherBuyer), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.PlacementConflict, (await f.Create().CreateAsync(Command(shops: [Shop]), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.PlacementConflict, (await f.Create().CreateAsync(Command(method: "sandbox"), CancellationToken.None)).ErrorCode);
        Assert.Single(f.Store.Orders);
    }

    [Fact]
    public async Task InvalidRecipientsAreRefusedWithFieldNames()
    {
        var f = new Fixture();
        var command = Command() with { Recipient = new NewOrderRecipient("", " ", "", null, "", null, null, "usa") };

        var result = await f.Create().CreateAsync(command, CancellationToken.None);

        foreach (var field in new[] { "recipientName", "recipientPhone", "address1", "city", "countryCode" }) Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(f.Store.Orders);
        Assert.Contains("recipientName", (await f.Create().CreateAsync(Command() with { Recipient = null }, CancellationToken.None)).Errors.Keys);
    }

    [Theory]
    [InlineData("", "cod", "placementKey")]
    [InlineData("k", "", "paymentMethod")]
    public async Task KeyAndPaymentMethodAreRequired(string key, string method, string field)
    {
        var f = new Fixture();

        var result = await f.Create().CreateAsync(Command(key, method: method), CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
    }

    [Fact]
    public async Task TheShopsOfAnOrderAreChecked()
    {
        var f = new Fixture();

        Assert.Contains("shops", (await f.Create().CreateAsync(Command() with { Shops = [] }, CancellationToken.None)).Errors.Keys);
        Assert.Contains("shops", (await f.Create().CreateAsync(Command(shops: [Shop, Shop]), CancellationToken.None)).Errors.Keys);
        Assert.Contains("shops", (await f.Create().CreateAsync(Command(shops: [Shop, 999]), CancellationToken.None)).Errors.Keys);
        Assert.Contains("shops", (await f.Create().CreateAsync(Command() with { Shops = Enumerable.Range(1, 21).Select(i => new NewShopOrder(i, "S", null, 0, [Line(1, 1, 1)])).ToList() }, CancellationToken.None)).Errors.Keys);
        Assert.Empty(f.Store.Orders);
    }

    [Theory]
    [InlineData(0, 1, 1, "quantity")]
    [InlineData(10_001, 1, 1, "quantity")]
    [InlineData(1, -1, 1, "unitPrice")]
    [InlineData(1, 1.005, 1, "unitPrice")]
    [InlineData(1, 1, -1, "shippingFee")]
    [InlineData(1, 1, 1.005, "shippingFee")]
    public async Task LinesAndFeesAreChecked(int quantity, double price, double fee, string field)
    {
        var f = new Fixture();
        var shop = new NewShopOrder(Shop, "Standard", null, (decimal)fee, [Line(1, quantity, (decimal)price)]);

        var result = await f.Create().CreateAsync(Command() with { Shops = [shop] }, CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(f.Store.Orders);
    }

    [Fact]
    public async Task EmptyAndOversizedLinesAndMissingMethodNamesAreRefused()
    {
        var f = new Fixture();

        Assert.Contains("lines", (await f.Create().CreateAsync(Command() with { Shops = [new NewShopOrder(Shop, "S", null, 0, [])] }, CancellationToken.None)).Errors.Keys);
        Assert.Contains("lines", (await f.Create().CreateAsync(Command() with { Shops = [new NewShopOrder(Shop, "S", null, 0, Enumerable.Range(1, 51).Select(i => Line(i, 1, 1)).ToList())] }, CancellationToken.None)).Errors.Keys);
        Assert.Contains("lines", (await f.Create().CreateAsync(Command() with { Shops = [new NewShopOrder(Shop, "S", null, 0, [Line(1, 1, 1) with { Name = " " }])] }, CancellationToken.None)).Errors.Keys);
        Assert.Contains("shippingMethodName", (await f.Create().CreateAsync(Command() with { Shops = [new NewShopOrder(Shop, "", null, 0, [Line(1, 1, 1)])] }, CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task AmountsFollowTheDecimalsOfTheCurrency()
    {
        var f = new Fixture();

        var result = await f.Create(decimals: 0).CreateAsync(Command(), CancellationToken.None);

        Assert.Contains("shippingFee", result.Errors.Keys);
        Assert.Contains("unitPrice", result.Errors.Keys);
    }

    // ---- Shop actions ----

    [Fact]
    public async Task AShopWalksItsOrderToDelivered()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = order.ShopOrders[0];

        var confirmed = await f.Create().ConfirmAsync(Shop, shop.Id, Seller, CancellationToken.None);
        Assert.Equal(ShopOrderStatus.Confirmed, confirmed.Value!.ShopOrder.Status);

        var shipped = await f.Create().ShipAsync(Shop, shop.Id, new ShipCommand(" DHL ", " 123 "), Seller, CancellationToken.None);
        Assert.Equal((ShopOrderStatus.Shipped, "DHL", "123"), (shipped.Value!.ShopOrder.Status, shipped.Value.ShopOrder.Carrier, shipped.Value.ShopOrder.TrackingNumber));

        var delivered = await f.Create().DeliverAsync(Shop, shop.Id, Seller, CancellationToken.None);
        Assert.Equal(ShopOrderStatus.Delivered, delivered.Value!.ShopOrder.Status);

        Assert.Equal(["Pending", "Confirmed", "Shipped", "Delivered"], delivered.Value.History.Select(h => h.ToStatus.ToString()));
        Assert.Equal(3, f.Audit.Events.Count(e => e == "order.shop_order_changed"));
        // The other shop's order is untouched.
        Assert.Equal(ShopOrderStatus.Pending, order.ShopOrders[1].Status);
    }

    [Theory]
    [InlineData("", "1", "carrier")]
    [InlineData("DHL", "", "trackingNumber")]
    public async Task ShippingNeedsCarrierAndTracking(string carrier, string tracking, string field)
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        Move(order, Shop, ShopOrderStatus.Confirmed);

        var result = await f.Create().ShipAsync(Shop, order.ShopOrders[0].Id, new ShipCommand(carrier, tracking), Seller, CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
        Assert.Equal(ShopOrderStatus.Confirmed, order.ShopOrders[0].Status);
    }

    [Fact]
    public async Task TrackingCanOnlyBeChangedWhileShipped()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = Move(order, Shop, ShopOrderStatus.Confirmed);

        Assert.Equal(OrderErrors.InvalidTransition, (await f.Create().UpdateTrackingAsync(Shop, shop.Id, new ShipCommand("DHL", "9"), Seller, CancellationToken.None)).ErrorCode);

        await f.Create().ShipAsync(Shop, shop.Id, new ShipCommand("DHL", "1"), Seller, CancellationToken.None);
        var changed = await f.Create().UpdateTrackingAsync(Shop, shop.Id, new ShipCommand("UPS", "2"), Seller, CancellationToken.None);

        Assert.Equal(("UPS", "2", ShopOrderStatus.Shipped), (changed.Value!.ShopOrder.Carrier, changed.Value.ShopOrder.TrackingNumber, changed.Value.ShopOrder.Status));
        Assert.Equal("UPS 2", changed.Value.History[^1].Note);
        Assert.Contains("carrier", (await f.Create().UpdateTrackingAsync(Shop, shop.Id, new ShipCommand("", "2"), Seller, CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task ASkippedStepIsRefused()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = order.ShopOrders[0];

        Assert.Equal(OrderErrors.InvalidTransition, (await f.Create().ShipAsync(Shop, shop.Id, new ShipCommand("DHL", "1"), Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.InvalidTransition, (await f.Create().DeliverAsync(Shop, shop.Id, Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(ShopOrderStatus.Pending, shop.Status);
        Assert.Single(f.Store.History, h => h.ShopOrderId == shop.Id);
    }

    [Fact]
    public async Task AShopCancelsBeforeShippingWithAReason()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = order.ShopOrders[0];

        Assert.Contains("reason", (await f.Create().CancelAsShopAsync(Shop, shop.Id, " ", Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("reason", (await f.Create().CancelAsShopAsync(Shop, shop.Id, new string('x', 501), Seller, CancellationToken.None)).Errors.Keys);

        var cancelled = await f.Create().CancelAsShopAsync(Shop, shop.Id, " Out of stock ", Seller, CancellationToken.None);

        Assert.Equal((ShopOrderStatus.Cancelled, "Out of stock"), (cancelled.Value!.ShopOrder.Status, cancelled.Value.ShopOrder.CancelReason));
        Assert.Equal("Out of stock", cancelled.Value.History[^1].Note);

        Move(order, Shop, ShopOrderStatus.Shipped);
        Assert.Equal(OrderErrors.InvalidTransition, (await f.Create().CancelAsShopAsync(Shop, shop.Id, "Late", Seller, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AShopOrderOfAnotherShopIsNotFoundForEveryAction()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var theirs = order.ShopOrders[1].Id;

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetShopOrderAsync(Shop, theirs, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().ConfirmAsync(Shop, theirs, Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().ShipAsync(Shop, theirs, new ShipCommand("a", "b"), Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateTrackingAsync(Shop, theirs, new ShipCommand("a", "b"), Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeliverAsync(Shop, theirs, Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().CancelAsShopAsync(Shop, theirs, "x", Seller, CancellationToken.None)).ErrorCode);
        Assert.Equal(ShopOrderStatus.Pending, order.ShopOrders[1].Status);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetShopOrderAsync(Shop, 999, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ALostRaceIsAnInvalidTransitionAndWritesNoHistory()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = order.ShopOrders[0];
        // The customer cancels between the shop's read and its change.
        f.Store.BeforeChange = s => { f.Store.BeforeChange = null; s.Status = ShopOrderStatus.Cancelled; };

        var result = await f.Create().ConfirmAsync(Shop, shop.Id, Seller, CancellationToken.None);

        Assert.Equal(OrderErrors.InvalidTransition, result.ErrorCode);
        Assert.Equal(ShopOrderStatus.Cancelled, shop.Status);
        Assert.Single(f.Store.History, h => h.ShopOrderId == shop.Id);
        Assert.DoesNotContain("order.shop_order_changed", f.Audit.Events);
    }

    [Fact]
    public async Task TheShopListAlwaysUsesTheShopOfTheRoute()
    {
        var f = new Fixture();
        await f.PlaceAsync();
        await f.PlaceAsync("k2", shops: [OtherShop]);

        var mine = await f.Create().GetShopOrdersAsync(Shop, new OrderListQuery(Buyer, OtherShop, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.All(mine.Items, s => Assert.Equal(Shop, s.VendorId));
        Assert.Single(mine.Items);
    }

    [Fact]
    public async Task CountsCoverEveryStatus()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        await f.PlaceAsync("k2", shops: [Shop]);
        Move(order, Shop, ShopOrderStatus.Shipped);

        var counts = await f.Create().GetCountsAsync(Shop, CancellationToken.None);

        Assert.Equal((1, 1, 0), (counts[ShopOrderStatus.Pending], counts[ShopOrderStatus.Shipped], counts[ShopOrderStatus.Cancelled]));
        Assert.Equal(Enum.GetValues<ShopOrderStatus>().Length, counts.Count);
    }

    // ---- Customer actions ----

    [Fact]
    public async Task ACustomerSeesOnlyTheirOwnOrders()
    {
        var f = new Fixture();
        var mine = await f.PlaceAsync("k1", Buyer);
        await f.PlaceAsync("k2", OtherBuyer);

        var list = await f.Create().GetMyOrdersAsync(Buyer, 0, 500, CancellationToken.None);
        var detail = await f.Create().GetMyOrderAsync(Buyer, mine.Id, CancellationToken.None);
        var foreign = await f.Create().GetMyOrderAsync(OtherBuyer, mine.Id, CancellationToken.None);

        Assert.Equal([mine.Id], list.Items.Select(o => o.Id));
        Assert.Equal((1, 100), (list.Page, list.PageSize));
        Assert.Equal(2, detail.Value!.History.Count);
        Assert.Equal(CatalogErrors.NotFound, foreign.ErrorCode);
    }

    [Fact]
    public async Task ACustomerCancelsOnlyWhilePending()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var first = order.ShopOrders[0];
        var second = order.ShopOrders[1];

        var cancelled = await f.Create().CancelAsCustomerAsync(Buyer, first.Id, "Changed my mind", CancellationToken.None);
        Move(order, OtherShop, ShopOrderStatus.Confirmed);

        Assert.Equal(ShopOrderStatus.Cancelled, cancelled.Value!.ShopOrder.Status);
        Assert.Equal(OrderActor.Customer, cancelled.Value.History[^1].Actor);
        Assert.Equal(OrderErrors.InvalidTransition, (await f.Create().CancelAsCustomerAsync(Buyer, second.Id, "Too late", CancellationToken.None)).ErrorCode);
        Assert.Contains("reason", (await f.Create().CancelAsCustomerAsync(Buyer, second.Id, "", CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task ACustomerConfirmsReceiptOnlyWhileShipped()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = order.ShopOrders[0];

        Assert.Equal(OrderErrors.InvalidTransition, (await f.Create().ConfirmReceiptAsync(Buyer, shop.Id, CancellationToken.None)).ErrorCode);
        Move(order, Shop, ShopOrderStatus.Shipped);

        var received = await f.Create().ConfirmReceiptAsync(Buyer, shop.Id, CancellationToken.None);

        Assert.Equal(ShopOrderStatus.Delivered, received.Value!.ShopOrder.Status);
    }

    [Fact]
    public async Task ACustomerCannotTouchAnotherCustomersShopOrder()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = order.ShopOrders[0];

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().CancelAsCustomerAsync(OtherBuyer, shop.Id, "x", CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().ConfirmReceiptAsync(OtherBuyer, shop.Id, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().CancelAsCustomerAsync(Buyer, 999, "x", CancellationToken.None)).ErrorCode);
        Assert.Equal(ShopOrderStatus.Pending, shop.Status);
    }

    // ---- Administrators ----

    [Theory]
    [InlineData(ShopOrderStatus.Pending, true)]
    [InlineData(ShopOrderStatus.Confirmed, true)]
    [InlineData(ShopOrderStatus.Shipped, true)]
    [InlineData(ShopOrderStatus.Delivered, true)]
    [InlineData(ShopOrderStatus.Completed, false)]
    [InlineData(ShopOrderStatus.Cancelled, false)]
    public async Task AnAdministratorCancelsUntilTheEnd(ShopOrderStatus status, bool allowed)
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();
        var shop = Move(order, Shop, status);

        var result = await f.Create().CancelAsAdminAsync(shop.Id, "Fraud check", Admin, CancellationToken.None);

        Assert.Equal(allowed, result.Succeeded);
        if (allowed) Assert.Equal((OrderActor.Admin, Admin, "Fraud check"), (result.Value!.History[^1].Actor, result.Value.History[^1].ActorCustomerId, result.Value.History[^1].Note));
        else Assert.Equal(OrderErrors.InvalidTransition, result.ErrorCode);
    }

    [Fact]
    public async Task AnAdministratorNeedsAReasonAndAnExistingShopOrder()
    {
        var f = new Fixture();
        var order = await f.PlaceAsync();

        Assert.Contains("reason", (await f.Create().CancelAsAdminAsync(order.ShopOrders[0].Id, null, Admin, CancellationToken.None)).Errors.Keys);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().CancelAsAdminAsync(999, "x", Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task TheAdministratorListFiltersByShopAndStatusAndNeverByCustomer()
    {
        var f = new Fixture();
        await f.PlaceAsync("k1", Buyer, Shop, OtherShop);
        var second = await f.PlaceAsync("k2", OtherBuyer, OtherShop);
        Move(second, OtherShop, ShopOrderStatus.Confirmed);

        var byShop = await f.Create().GetOrdersAsync(new OrderListQuery(Buyer, Shop, null, null, null, null, 1, 20), CancellationToken.None);
        var byStatus = await f.Create().GetOrdersAsync(new OrderListQuery(null, null, ShopOrderStatus.Confirmed, null, null, null, 1, 20), CancellationToken.None);
        var everything = await f.Create().GetOrdersAsync(new OrderListQuery(Buyer, null, null, null, null, null, 1, 20), CancellationToken.None);

        Assert.Single(byShop.Items);
        Assert.Equal([second.Id], byStatus.Items.Select(o => o.Id));
        Assert.Equal(2, everything.TotalCount);
        var newest = (await f.Create().GetOrdersAsync(new OrderListQuery(null, null, null, null, null, null, 1, 1), CancellationToken.None)).Items[0];
        Assert.Single((await f.Create().GetOrderAsync(newest.Id, CancellationToken.None)).Value!.Order.ShopOrders);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().GetOrderAsync(999, CancellationToken.None)).ErrorCode);
    }
}
