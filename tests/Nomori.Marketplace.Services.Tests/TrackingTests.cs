using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Services.Shipping;

namespace Nomori.Marketplace.Services.Tests;

public sealed class TrackingTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private const int Buyer = 100;
    private const int OtherBuyer = 101;
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;
    private const int Admin = 1;
    private const int ShopOrderId = 1;

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FakeTrackingStore : ITrackingStore
    {
        private int nextCarrier = 1;
        private long nextEvent = 1;

        public List<Carrier> Carriers { get; } = [];
        public List<TrackingEvent> Events { get; } = [];

        public Task<IReadOnlyList<Carrier>> ListCarriersAsync(bool activeOnly, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Carrier>>(Carriers.Where(c => !activeOnly || c.Active).OrderBy(c => c.DisplayOrder).ToList());

        public Task<Carrier?> GetCarrierAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Carriers.FirstOrDefault(c => c.Id == id));

        public Task<Carrier?> FindActiveCarrierByNameAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(Carriers.FirstOrDefault(c => c.Active && c.Name == name));

        public Task<int> CountCarriersAsync(CancellationToken cancellationToken) => Task.FromResult(Carriers.Count);

        public Task<bool> CarrierNameExistsAsync(string name, int excludeId, CancellationToken cancellationToken) =>
            Task.FromResult(Carriers.Any(c => c.Id != excludeId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<int> InsertCarrierAsync(Carrier carrier, CancellationToken cancellationToken)
        {
            carrier.Id = nextCarrier++;
            Carriers.Add(carrier);
            return Task.FromResult(carrier.Id);
        }

        public Task UpdateCarrierAsync(Carrier carrier, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteCarrierAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Carriers.RemoveAll(c => c.Id == id) == 1);

        public Task<long?> TryAddEventAsync(TrackingEvent trackingEvent, int maxEvents, CancellationToken cancellationToken)
        {
            if (Events.Count(e => e.ShopOrderId == trackingEvent.ShopOrderId) >= maxEvents) return Task.FromResult<long?>(null);
            trackingEvent.Id = nextEvent++;
            Events.Add(trackingEvent);
            return Task.FromResult<long?>(trackingEvent.Id);
        }

        public Task<IReadOnlyList<TrackingEvent>> ListEventsAsync(int shopOrderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TrackingEvent>>(Events.Where(e => e.ShopOrderId == shopOrderId).OrderByDescending(e => e.OccurredOnUtc).ThenByDescending(e => e.Id).ToList());
    }

    private sealed class Fixture
    {
        public FakeTrackingStore Store { get; } = new();
        public OrderTests.FakeOrderStore Orders { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture(ShopOrderStatus status = ShopOrderStatus.Shipped, bool awaitingPayment = false)
        {
            Orders.Orders.Add(new Order
            {
                Id = 1, Number = "NM-1", CustomerId = Buyer, AwaitingPayment = awaitingPayment, CreatedOnUtc = Now.AddDays(-3),
                ShopOrders =
                [
                    new ShopOrder
                    {
                        Id = ShopOrderId, OrderId = 1, Number = "NM-1-1", VendorId = Shop, Status = status, Carrier = "DHL", TrackingNumber = "AB 12/3",
                        CreatedOnUtc = Now.AddDays(-3)
                    }
                ]
            });
            Store.Carriers.Add(new Carrier { Id = 90, Name = "DHL", TrackingUrlTemplate = "https://track.example/?n={tracking}", Active = true });
            Store.Carriers.Add(new Carrier { Id = 91, Name = "Local", Active = true });
        }

        public TrackingService Create() => new(Store, Orders, Audit, new FixedClock());
    }

    private static AddTrackingEventCommand Event(string status = "in_transit", DateTime? at = null) => new(status, "Hanoi hub", "left the hub", at);

    // ---- Rules ----

    [Theory]
    [InlineData("https://t.example/?n={tracking}", true)]
    [InlineData("http://t.example/{tracking}", true)]
    [InlineData("https://t.example/", false)]
    [InlineData("javascript:alert({tracking})", false)]
    [InlineData("ftp://t.example/{tracking}", false)]
    [InlineData("/relative/{tracking}", false)]
    [InlineData("", false)]
    public void ATemplateIsAWebAddressWithThePlaceholder(string template, bool valid) =>
        Assert.Equal(valid, TrackingRules.IsValidTemplate(template));

    [Fact]
    public void TheTrackingNumberIsEscapedInTheLink()
    {
        Assert.Equal("https://t.example/?n=AB%2012%2F3%26x", TrackingRules.UrlFor("https://t.example/?n={tracking}", " AB 12/3&x "));
        Assert.Null(TrackingRules.UrlFor(null, "123"));
        Assert.Null(TrackingRules.UrlFor("https://t.example/{tracking}", " "));
    }

    // ---- Carriers ----

    [Fact]
    public async Task ACarrierIsValidatedAndItsNameIsUnique()
    {
        var f = new Fixture();
        var service = f.Create();

        var noName = await service.CreateCarrierAsync(new SaveCarrierCommand(" ", null, true, 0), Admin, CancellationToken.None);
        var badTemplate = await service.CreateCarrierAsync(new SaveCarrierCommand("X", "javascript:alert(1)", true, 0), Admin, CancellationToken.None);
        var ok = await service.CreateCarrierAsync(new SaveCarrierCommand(" Express ", "https://e.example/{tracking}", true, 3), Admin, CancellationToken.None);
        var duplicate = await service.CreateCarrierAsync(new SaveCarrierCommand("express", null, true, 0), Admin, CancellationToken.None);

        Assert.Contains("name", noName.Errors.Keys);
        Assert.Contains("trackingUrlTemplate", badTemplate.Errors.Keys);
        Assert.True(ok.Succeeded);
        Assert.Equal("Express", ok.Value!.Name);
        Assert.Equal(TrackingErrors.CarrierNameExists, duplicate.ErrorCode);
        Assert.Contains("carrier.created", f.Audit.Events);
    }

    [Fact]
    public async Task TheNumberOfCarriersIsLimited()
    {
        var f = new Fixture();
        for (var i = 0; i < TrackingLimits.MaxCarriers - 2; i++) f.Store.Carriers.Add(new Carrier { Id = 200 + i, Name = $"C{i}" });

        var result = await f.Create().CreateCarrierAsync(new SaveCarrierCommand("One more", null, true, 0), Admin, CancellationToken.None);

        Assert.Equal(TrackingErrors.CarrierLimit, result.ErrorCode);
    }

    [Fact]
    public async Task ACarrierCanBeUpdatedAndDeletedAndAnUnknownOneIsNotFound()
    {
        var f = new Fixture();
        var service = f.Create();

        var updated = await service.UpdateCarrierAsync(91, new SaveCarrierCommand("Local Post", null, false, 2), Admin, CancellationToken.None);
        var taken = await service.UpdateCarrierAsync(91, new SaveCarrierCommand("dhl", null, true, 0), Admin, CancellationToken.None);
        var missing = await service.UpdateCarrierAsync(999, new SaveCarrierCommand("Z", null, true, 0), Admin, CancellationToken.None);
        var deleted = await service.DeleteCarrierAsync(91, Admin, CancellationToken.None);
        var again = await service.DeleteCarrierAsync(91, Admin, CancellationToken.None);

        Assert.False(updated.Value!.Active);
        Assert.Equal(TrackingErrors.CarrierNameExists, taken.ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, missing.ErrorCode);
        Assert.True(deleted.Succeeded);
        Assert.Equal(CatalogErrors.NotFound, again.ErrorCode);
    }

    [Fact]
    public async Task OnlyActiveCarriersAreOfferedToShops()
    {
        var f = new Fixture();
        f.Store.Carriers[1].Active = false;

        Assert.Single(await f.Create().GetCarriersAsync(true, CancellationToken.None));
        Assert.Equal(2, (await f.Create().GetCarriersAsync(false, CancellationToken.None)).Count);
    }

    // ---- Seeing the parcel ----

    [Fact]
    public async Task TheBuyerTheShopAndAnAdministratorSeeTheSameParcelWithALink()
    {
        var f = new Fixture();
        f.Store.Events.Add(new TrackingEvent { Id = 1, ShopOrderId = ShopOrderId, Status = "in_transit", OccurredOnUtc = Now.AddHours(-5) });
        var service = f.Create();

        foreach (var caller in new[] { new TrackingCaller(false, Buyer, null), new TrackingCaller(false, Seller, Shop), new TrackingCaller(true, Admin, null) })
        {
            var result = await service.GetTrackingAsync(caller, ShopOrderId, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal("https://track.example/?n=AB%2012%2F3", result.Value!.TrackingUrl);
            Assert.Single(result.Value.Events);
            Assert.Equal("shipped", result.Value.OrderStatus);
        }
    }

    [Fact]
    public async Task SomeoneElseDoesNotSeeTheParcel()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.Equal(CatalogErrors.NotFound, (await service.GetTrackingAsync(new TrackingCaller(false, OtherBuyer, null), ShopOrderId, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.GetTrackingAsync(new TrackingCaller(false, Seller, OtherShop), ShopOrderId, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.GetTrackingAsync(new TrackingCaller(true, Admin, null), 999, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AShopCannotSeeAnOrderThatStillAwaitsItsPaymentButTheBuyerCan()
    {
        var f = new Fixture(awaitingPayment: true);
        var service = f.Create();

        Assert.Equal(CatalogErrors.NotFound, (await service.GetTrackingAsync(new TrackingCaller(false, Seller, Shop), ShopOrderId, CancellationToken.None)).ErrorCode);
        Assert.True((await service.GetTrackingAsync(new TrackingCaller(false, Buyer, null), ShopOrderId, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ACarrierWithoutATemplateOrAnInactiveOneGivesNoLink()
    {
        var f = new Fixture();
        var order = f.Orders.Orders[0].ShopOrders[0];
        order.Carrier = "Local";
        var service = f.Create();
        Assert.Null((await service.GetTrackingAsync(new TrackingCaller(true, Admin, null), ShopOrderId, CancellationToken.None)).Value!.TrackingUrl);

        order.Carrier = "DHL";
        f.Store.Carriers[0].Active = false;
        Assert.Null((await service.GetTrackingAsync(new TrackingCaller(true, Admin, null), ShopOrderId, CancellationToken.None)).Value!.TrackingUrl);
    }

    // ---- Telling where the parcel is ----

    [Fact]
    public async Task AShopAddsEventsAndTheNewestComesFirst()
    {
        var f = new Fixture();
        var service = f.Create();

        await service.AddEventAsync(Shop, ShopOrderId, Event("picked_up", Now.AddHours(-6)), Seller, CancellationToken.None);
        var result = await service.AddEventAsync(Shop, ShopOrderId, Event("in_transit", Now.AddHours(-1)), Seller, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["in_transit", "picked_up"], result.Value!.Events.Select(e => e.Status));
        Assert.Contains("shipping.tracking_event", f.Audit.Events);
    }

    [Fact]
    public async Task AnEventWithoutATimeIsDatedNow()
    {
        var f = new Fixture();

        var result = await f.Create().AddEventAsync(Shop, ShopOrderId, new AddTrackingEventCommand(" In_Transit ", null, null, null), Seller, CancellationToken.None);

        Assert.Equal(Now, result.Value!.Events.Single().OccurredOnUtc);
        Assert.Equal("in_transit", result.Value.Events.Single().Status);
    }

    [Fact]
    public async Task AnEventIsValidated()
    {
        var f = new Fixture();
        var service = f.Create();

        var status = await service.AddEventAsync(Shop, ShopOrderId, Event("teleported"), Seller, CancellationToken.None);
        var future = await service.AddEventAsync(Shop, ShopOrderId, Event(at: Now.AddHours(2)), Seller, CancellationToken.None);
        var tooEarly = await service.AddEventAsync(Shop, ShopOrderId, Event(at: Now.AddDays(-10)), Seller, CancellationToken.None);
        var longNote = await service.AddEventAsync(Shop, ShopOrderId, new AddTrackingEventCommand("info", null, new string('x', 501), null), Seller, CancellationToken.None);

        Assert.Contains("status", status.Errors.Keys);
        Assert.Contains("occurredOnUtc", future.Errors.Keys);
        Assert.Contains("occurredOnUtc", tooEarly.Errors.Keys);
        Assert.Contains("note", longNote.Errors.Keys);
        Assert.Empty(f.Store.Events);
    }

    [Theory]
    [InlineData(ShopOrderStatus.Pending)]
    [InlineData(ShopOrderStatus.Confirmed)]
    [InlineData(ShopOrderStatus.Delivered)]
    [InlineData(ShopOrderStatus.Completed)]
    [InlineData(ShopOrderStatus.Cancelled)]
    public async Task EventsAreOnlyAddedWhileTheParcelIsOnItsWay(ShopOrderStatus status)
    {
        var f = new Fixture(status);

        var result = await f.Create().AddEventAsync(Shop, ShopOrderId, Event(), Seller, CancellationToken.None);

        Assert.Equal(TrackingErrors.NotShipped, result.ErrorCode);
        Assert.Empty(f.Store.Events);
    }

    [Fact]
    public async Task AnotherShopCannotAddEventsAndAnUnpaidOrderIsNotFound()
    {
        var f = new Fixture();
        var other = await f.Create().AddEventAsync(OtherShop, ShopOrderId, Event(), Seller, CancellationToken.None);
        var unpaid = await new Fixture(awaitingPayment: true).Create().AddEventAsync(Shop, ShopOrderId, Event(), Seller, CancellationToken.None);

        Assert.Equal(CatalogErrors.NotFound, other.ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, unpaid.ErrorCode);
    }

    [Fact]
    public async Task TheNumberOfEventsPerParcelIsLimited()
    {
        var f = new Fixture();
        var service = f.Create();
        for (var i = 0; i < TrackingLimits.MaxEventsPerShopOrder; i++)
            Assert.True((await service.AddEventAsync(Shop, ShopOrderId, Event(at: Now.AddMinutes(-i - 1)), Seller, CancellationToken.None)).Succeeded);

        var result = await service.AddEventAsync(Shop, ShopOrderId, Event(), Seller, CancellationToken.None);

        Assert.Equal(TrackingErrors.EventLimit, result.ErrorCode);
    }
}
