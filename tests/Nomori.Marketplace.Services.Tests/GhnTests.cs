using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Services.Directory;
using Nomori.Marketplace.Services.Shipping;

namespace Nomori.Marketplace.Services.Tests;

public sealed class GhnTests
{
    private const int Buyer = 100;

    /// <summary>GHN that quotes a fixed fee per gram and records what it was asked.</summary>
    private sealed class FakeGhnClient : IGhnClient
    {
        public List<GhnFeeRequest> FeeRequests { get; } = [];
        public string? FeeError { get; set; }

        public Task<GhnResult<long>> GetFeeAsync(GhnFeeRequest request, CancellationToken cancellationToken)
        {
            FeeRequests.Add(request);
            return Task.FromResult(FeeError is null ? GhnResult.Ok(20_000L + request.WeightGrams * 10L) : GhnResult.Fail<long>(FeeError));
        }

        public Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnDistrict>>> GetDistrictsAsync(int provinceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int districtId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<GhnCreatedOrder>> CreateOrderAsync(GhnCreateOrderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<GhnOrderDetail>> GetOrderAsync(string orderCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<string>> GetLabelUrlAsync(string orderCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static GhnOptions Configured(bool enabled = true) => new() { Enabled = enabled, Token = "t", ShopId = 1, ItemWeightGrams = 500, VndPerPrimaryUnit = 25_000m };

    private static GhnShippingService Service(FakeGhnClient client, GhnOptions settings, FakeCurrencyStore? currencies = null, FakeGhnStore? store = null) =>
        new(client, store ?? new FakeGhnStore(), null!, null!, new FakePrimaryCurrency(),
            new CurrencyService(currencies ?? new FakeCurrencyStore(), new RecordingAuditLog(), new TestClock()), new RecordingAuditLog(), new TestClock(),
            Options.Create(settings));

    private const int PickupShop = 5;

    // ---- Rules ----

    [Theory]
    [InlineData("picked", TrackingRules.PickedUp)]
    [InlineData("transporting", TrackingRules.InTransit)]
    [InlineData("delivering", TrackingRules.OutForDelivery)]
    [InlineData("delivery_fail", TrackingRules.DeliveryAttempted)]
    [InlineData("returned", TrackingRules.Exception)]
    [InlineData("ready_to_pick", TrackingRules.Info)]
    [InlineData("something_new", TrackingRules.Info)]
    public void GhnStatusesBecomeTrackingStatuses(string ghn, string expected) => Assert.Equal(expected, GhnRules.TrackingStatusOf(ghn));

    [Fact]
    public void DeliveredMovesTheOrderInsteadOfAddingALineAndEveryUnitWeighsTheSame()
    {
        Assert.Null(GhnRules.TrackingStatusOf("delivered"));
        Assert.Equal("GHN: Giao hàng thất bại", GhnRules.NoteOf("delivery_fail"));
        Assert.Equal(1500, GhnRules.WeightOf([1, 2], 500));
        Assert.Equal(1, GhnRules.WeightOf([], 500));
        Assert.False(new GhnOptions { Enabled = true, Token = "t" }.IsConfigured);
        Assert.True(Configured().IsConfigured);
    }

    // ---- Quote ----

    [Fact]
    public async Task AQuoteWeighsTheUnitsAndConvertsGhnsVndToThePrimaryCurrency()
    {
        var client = new FakeGhnClient();

        var option = await Service(client, Configured()).QuoteAsync(PickupShop, 1442, "20101", [1, 2], CancellationToken.None);

        // 3 units of 500 g: 20,000 + 15,000 VND = 35,000 VND, at 25,000 VND a dollar; priced from the shop's own pickup address.
        Assert.Equal((1442, "20101", 1500), (client.FeeRequests[0].ToDistrictId, client.FeeRequests[0].ToWardCode, client.FeeRequests[0].WeightGrams));
        Assert.Equal((1805, "1B2302"), (client.FeeRequests[0].FromDistrictId, client.FeeRequests[0].FromWardCode));
        Assert.Equal((GhnRules.RateId, 1.40m, false), (option!.RateId, option.Fee, option.IsFree));
    }

    [Fact]
    public async Task AVndCurrencyGivesTheRate()
    {
        var currencies = new FakeCurrencyStore();
        currencies.Currencies.Add(new Currency { Id = 2, Code = "VND", Name = "Dong", Symbol = "₫", DecimalPlaces = 0, RateToPrimary = 35_000m, Published = true });

        var option = await Service(new FakeGhnClient(), Configured(), currencies).QuoteAsync(PickupShop, 1, "1", [1], CancellationToken.None);

        Assert.Equal(Math.Round(25_000m / 35_000m, 2), option!.Fee);
    }

    [Fact]
    public async Task NoQuoteWhenGhnIsOffOrRefuses()
    {
        var client = new FakeGhnClient();
        Assert.Null(await Service(client, Configured(enabled: false)).QuoteAsync(PickupShop, 1, "1", [1], CancellationToken.None));
        // A shop that has not given a pickup address is not offered GHN, and GHN is not even asked.
        Assert.Null(await Service(client, Configured()).QuoteAsync(PickupShop + 1, 1, "1", [1], CancellationToken.None));
        Assert.Empty(client.FeeRequests);

        client.FeeError = "Route not supported";
        Assert.Null(await Service(client, Configured()).QuoteAsync(PickupShop, 1, "1", [1], CancellationToken.None));
    }

    // ---- The shipping quote offers GHN for an address with GHN's area ----

    private sealed class FakeGhn(ShippingOption? option) : IGhnShippingService
    {
        public List<(int District, string Ward, int Units)> Asked { get; } = [];
        public bool IsConfigured => true;

        public Task<ShippingOption?> QuoteAsync(int vendorId, int districtId, string wardCode, IReadOnlyList<int> quantities, CancellationToken cancellationToken)
        {
            Asked.Add((districtId, wardCode, quantities.Sum()));
            return Task.FromResult(option);
        }

        public Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnDistrict>>> GetDistrictsAsync(int provinceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int districtId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Core.Orders.ShopOrderDetail>> CreateShipmentAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Core.Orders.ShopOrderDetail>> SyncAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<string>> GetLabelUrlAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool IsWebhookSecret(string? secret) => throw new NotSupportedException();
        public Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<GhnPickup>> SavePickupAsync(int vendorId, SaveGhnPickupCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeletePickupAsync(int vendorId, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnWebhookOutcome> HandleWebhookAsync(GhnWebhook webhook, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static CartLineView Line(int id, int vendor, int quantity) =>
        new(id, id, "P" + id, vendor, "Shop", 0, null, null, quantity, 10m, null, 10m * quantity, PriceRule.Base, null, null, []);

    private static async Task<ShippingQuote> QuoteFor(FakeGhn ghn, CustomerAddress address)
    {
        var directory = new DirectoryTests.FakeDirectoryStore();
        directory.Countries.Add(new Country { Id = 1, Code = "VN", Name = "Viet Nam", Published = true, AllowsBilling = true, AllowsShipping = true });
        var addresses = new ShippingTests.StubAddresses();
        addresses.Addresses.Add(address);
        var cart = new ShippingTests.StubCart { View = new CartView("USD", [new CartShopGroup(5, "Shop", [Line(1, 5, 2), Line(2, 5, 1)], 30m)], 30m, 3, true) };
        var audit = new RecordingAuditLog();
        var service = new ShippingService(new ShippingTests.FakeShippingStore(), new DirectoryService(directory, audit, new TestClock()), cart, addresses,
            new FakePrimaryCurrency(), audit, new TestClock(), ghn);
        return (await service.QuoteAsync(Buyer, new ShippingQuoteRequest(address.Id, null, null), CancellationToken.None)).Value!;
    }

    private static CustomerAddress Address(int? district = 1442, string? ward = "20101") => new()
    {
        Id = 9, CustomerId = Buyer, FirstName = "A", LastName = "B", Address1 = "1 Le Loi", City = "HCM", CountryCode = "VN", PhoneNumber = "0900000000",
        GhnProvinceId = district is null ? null : 202, GhnDistrictId = district, GhnWardCode = ward
    };

    [Fact]
    public async Task AnAddressWithGhnsAreaGetsTheGhnOptionNextToTheShopsRates()
    {
        var ghn = new FakeGhn(new ShippingOption(GhnRules.RateId, GhnRules.MethodName, 1.4m, false, null, null));

        var quote = await QuoteFor(ghn, Address());

        Assert.Equal([(1442, "20101", 3)], ghn.Asked);
        Assert.Equal([GhnRules.RateId], quote.Shops[0].Options.Select(o => o.RateId));
        Assert.True(quote.CanShipAll);
        Assert.Equal(1.4m, quote.ShippingTotal);
    }

    [Fact]
    public async Task AnAddressWithoutGhnsAreaIsNotQuotedByGhn()
    {
        var ghn = new FakeGhn(new ShippingOption(GhnRules.RateId, GhnRules.MethodName, 1.4m, false, null, null));

        var quote = await QuoteFor(ghn, Address(district: null, ward: null));

        Assert.Empty(ghn.Asked);
        Assert.False(quote.CanShipAll);
    }

    // ---- Webhook ----

    private sealed class FakeGhnStore : IGhnStore
    {
        public Dictionary<string, GhnShopOrderRef> Orders { get; } = new() { ["L8RLBD"] = new GhnShopOrderRef(9, 3) };

        public Dictionary<int, GhnPickup> Pickups { get; } = new()
        {
            [PickupShop] = new GhnPickup
            {
                VendorId = PickupShop, ContactName = "Meo", Phone = "0901234567", Address = "1 Hang Bac", ProvinceId = 201, ProvinceName = "Hà Nội",
                DistrictId = 1805, DistrictName = "Quận Hoàn Kiếm", WardCode = "1B2302", WardName = "Phường Hàng Bạc"
            }
        };

        public Task<GhnShopOrderRef?> FindByOrderCodeAsync(string orderCode, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.GetValueOrDefault(orderCode));

        public Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken) => Task.FromResult(Pickups.GetValueOrDefault(vendorId));

        public Task SavePickupAsync(GhnPickup pickup, CancellationToken cancellationToken)
        {
            Pickups[pickup.VendorId] = pickup;
            return Task.CompletedTask;
        }

        public Task<bool> DeletePickupAsync(int vendorId, CancellationToken cancellationToken) => Task.FromResult(Pickups.Remove(vendorId));

        public Task<GhnDestination?> GetDestinationAsync(int orderId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>Tracking lines in memory; records who wrote each one.</summary>
    private sealed class FakeTracking : ITrackingService
    {
        public List<(int VendorId, int ShopOrderId, AddTrackingEventCommand Command, int Actor)> Added { get; } = [];

        public Task<CatalogResult<TrackingView>> GetTrackingAsync(TrackingCaller caller, int shopOrderId, CancellationToken cancellationToken) =>
            Task.FromResult(CatalogResult.Success(new TrackingView(shopOrderId, GhnRules.CarrierName, "L8RLBD", null, "shipped",
                Added.Where(a => a.ShopOrderId == shopOrderId)
                    .Select(a => new TrackingEvent { ShopOrderId = shopOrderId, Status = a.Command.Status!, Note = a.Command.Note, OccurredOnUtc = a.Command.OccurredOnUtc ?? default })
                    .ToList())));

        public Task<CatalogResult<TrackingView>> AddEventAsync(int vendorId, int shopOrderId, AddTrackingEventCommand command, int actorCustomerId, CancellationToken cancellationToken)
        {
            Added.Add((vendorId, shopOrderId, command, actorCustomerId));
            return GetTrackingAsync(new TrackingCaller(true, 0, null), shopOrderId, cancellationToken);
        }

        public Task<IReadOnlyList<Carrier>> GetCarriersAsync(bool activeOnly, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Carrier>> CreateCarrierAsync(SaveCarrierCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<Carrier>> UpdateCarrierAsync(int id, SaveCarrierCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteCarrierAsync(int id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class WebhookFixture
    {
        public FakeTracking Tracking { get; } = new();
        public PaymentJobTests.FakeOrders Orders { get; } = new();

        public GhnShippingService Create(string secret = "s3cret") =>
            new(new FakeGhnClient(), new FakeGhnStore(), Orders, Tracking, new FakePrimaryCurrency(),
                new CurrencyService(new FakeCurrencyStore(), new RecordingAuditLog(), new TestClock()), new RecordingAuditLog(), new TestClock(),
                Options.Create(new GhnOptions { Enabled = true, Token = "t", ShopId = 229760, WebhookSecret = secret }));
    }

    private static GhnWebhook Hook(string status, string type = "switch_status", int shop = 229760, string code = "L8RLBD", string time = "2026-01-02T03:04:05Z") =>
        new(shop, type, code, status, time, null, null);

    [Fact]
    public void TheWebhookNeedsTheConfiguredSecretAndIsOffWithoutOne()
    {
        var f = new WebhookFixture();

        Assert.True(f.Create().IsWebhookSecret("s3cret"));
        Assert.False(f.Create().IsWebhookSecret("wrong"));
        Assert.False(f.Create().IsWebhookSecret(null));
        Assert.False(f.Create(secret: "").IsWebhookSecret(""));
    }

    [Fact]
    public async Task AStatusChangeBecomesATrackingLineWrittenByTheSystemOnce()
    {
        var f = new WebhookFixture();

        Assert.Equal(GhnWebhookOutcome.Handled, await f.Create().HandleWebhookAsync(Hook("picked"), CancellationToken.None));
        // GHN sends the same event again: nothing more.
        await f.Create().HandleWebhookAsync(Hook("picked"), CancellationToken.None);

        var line = Assert.Single(f.Tracking.Added);
        Assert.Equal((3, 9, 0), (line.VendorId, line.ShopOrderId, line.Actor));
        Assert.Equal((TrackingRules.PickedUp, "GHN: Đã lấy hàng", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            (line.Command.Status, line.Command.Note, line.Command.OccurredOnUtc));
        Assert.Empty(f.Orders.DeliveredByCarrier);
    }

    [Fact]
    public async Task DeliveredMarksTheShopOrderDeliveredByTheCarrier()
    {
        var f = new WebhookFixture();

        await f.Create().HandleWebhookAsync(Hook("delivered"), CancellationToken.None);

        Assert.Equal([(9, "GHN: Giao hàng thành công")], f.Orders.DeliveredByCarrier);
        // The journey ends with the delivery, written before the order leaves "shipped".
        var line = Assert.Single(f.Tracking.Added);
        Assert.Equal((TrackingRules.Info, "GHN: Giao hàng thành công"), (line.Command.Status, line.Command.Note));
    }

    [Fact]
    public async Task OtherEventsShopsAndOrdersAreLeftAlone()
    {
        var f = new WebhookFixture();

        Assert.Equal(GhnWebhookOutcome.Handled, await f.Create().HandleWebhookAsync(Hook("picked", type: "update_weight"), CancellationToken.None));
        Assert.Equal(GhnWebhookOutcome.Ignored, await f.Create().HandleWebhookAsync(Hook("picked", shop: 1), CancellationToken.None));
        Assert.Equal(GhnWebhookOutcome.Ignored, await f.Create().HandleWebhookAsync(Hook("delivered", code: "NOPE"), CancellationToken.None));

        Assert.Empty(f.Tracking.Added);
        Assert.Empty(f.Orders.DeliveredByCarrier);
    }

    // ---- The shop's pickup address ----

    [Theory]
    [InlineData("0901234567", true)]
    [InlineData("+84901234567", true)]
    [InlineData("84901234567", true)]
    [InlineData("02438123456", true)]
    [InlineData("901234567", false)]
    [InlineData("0901-234", false)]
    [InlineData("+1 555 0100", false)]
    public void PhonesFollowTheVietnameseShape(string phone, bool valid) => Assert.Equal(valid, GhnRules.IsVietnamesePhone(phone));

    [Fact]
    public async Task APickupAddressIsCheckedSavedAndDeleted()
    {
        var store = new FakeGhnStore();
        var service = Service(new FakeGhnClient(), Configured(), store: store);

        var bad = await service.SavePickupAsync(7, new SaveGhnPickupCommand(" ", "123", "", 0, null, 0, null, null, null), 1, CancellationToken.None);
        Assert.Equal(["address", "contactName", "phone", "wardCode"], bad.Errors.Keys.Order());

        var saved = await service.SavePickupAsync(7, new SaveGhnPickupCommand(
            " Meo ", "090 123 4567", " 12 Nguyễn Huệ ", 202, "Hồ Chí Minh", 1442, "Quận 1", "20101", "Phường Bến Nghé"), 1, CancellationToken.None);
        Assert.True(saved.Succeeded);
        Assert.Equal(("Meo", "0901234567", "12 Nguyễn Huệ", 1442), (store.Pickups[7].ContactName, store.Pickups[7].Phone, store.Pickups[7].Address, store.Pickups[7].DistrictId));
        Assert.Equal(new GhnSender("Meo", "0901234567", "12 Nguyễn Huệ", "Phường Bến Nghé", "Quận 1", "Hồ Chí Minh"), store.Pickups[7].ToSender());

        await service.DeletePickupAsync(7, 1, CancellationToken.None);
        Assert.False(store.Pickups.ContainsKey(7));
    }
}
