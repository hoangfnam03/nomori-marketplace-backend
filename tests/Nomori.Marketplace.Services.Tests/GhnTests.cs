using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Services.Catalog;
using Nomori.Marketplace.Services.Directory;
using Nomori.Marketplace.Services.Shipping;

namespace Nomori.Marketplace.Services.Tests;

public sealed class GhnTests
{
    private const int Buyer = 100;
    private const int PickupShop = 5;

    // Two-level places (province, ward): the shop picks up in Hanoi, the customer is in Ho Chi Minh City.
    private static readonly GhnPlace Hanoi = new(1000000, 1003445);
    private static readonly GhnPlace Saigon = new(1000001, 1003506);

    /// <summary>GHN that quotes a fixed fee per gram (plus 1% of the declared value), gives a two-day window, and records what it was asked.</summary>
    private sealed class FakeGhnClient : IGhnClient
    {
        public List<GhnFeeRequest> FeeRequests { get; } = [];
        public string? FeeError { get; set; }
        public bool LeadtimeFails { get; set; }

        public Task<GhnResult<long>> GetFeeAsync(GhnFeeRequest request, CancellationToken cancellationToken)
        {
            FeeRequests.Add(request);
            return Task.FromResult(FeeError is null
                ? GhnResult.Ok(20_000L + request.Parcel.WeightGrams * 10L + request.InsuranceVnd / 100)
                : GhnResult.Fail<long>(FeeError));
        }

        public Task<GhnResult<GhnLeadtime>> GetLeadtimeAsync(GhnFeeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(LeadtimeFails
                ? GhnResult.Fail<GhnLeadtime>("no leadtime")
                : GhnResult.Ok(new GhnLeadtime(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc))));

        public Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int provinceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<GhnCreatedOrder>> CreateOrderAsync(GhnCreateOrderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<GhnOrderDetail>> GetOrderAsync(string orderCode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<string>> GetLabelUrlAsync(string orderCode, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeGhnStore : IGhnStore
    {
        public Dictionary<string, GhnShopOrderRef> Orders { get; } = new()
        {
            // Declared at 600 g for a GHN fee of 1.00 (25,000 VND); items 40, no discount.
            ["L8RLBD"] = new GhnShopOrderRef(9, 3, 77, 40m, 0m, 600, 1.00m)
        };

        public Dictionary<int, GhnPickup> Pickups { get; } = new()
        {
            [PickupShop] = new GhnPickup
            {
                VendorId = PickupShop, ContactName = "Meo", Phone = "0901234567", Address = "1 Hang Bac", ProvinceId = Hanoi.ProvinceId, ProvinceName = "Hà Nội",
                WardId = Hanoi.WardId, WardName = "Phường Hoàn Kiếm"
            },
            // The shop of the order L8RLBD.
            [3] = new GhnPickup
            {
                VendorId = 3, ContactName = "Shop", Phone = "0901234567", Address = "2 Le Loi", ProvinceId = Hanoi.ProvinceId, ProvinceName = "Hà Nội",
                WardId = Hanoi.WardId, WardName = "Phường Hoàn Kiếm"
            }
        };

        /// <summary>Products without an entry have no weight or size (saved before products carried them).</summary>
        public Dictionary<int, GhnProductSize> Sizes { get; } = [];

        public Dictionary<int, decimal> Adjustments { get; } = [];
        public Dictionary<int, DateTime> CodTransfers { get; } = [];

        public Task<GhnShopOrderRef?> FindByOrderCodeAsync(string orderCode, CancellationToken cancellationToken) => Task.FromResult(Orders.GetValueOrDefault(orderCode));

        public Task<GhnDestination?> GetDestinationAsync(int orderId, CancellationToken cancellationToken) =>
            Task.FromResult<GhnDestination?>(new GhnDestination("Khach", "0903428409", "1 Le Loi", null, "HCM", null, "cod",
                Saigon.ProvinceId, "Hồ Chí Minh", Saigon.WardId, "Phường Sài Gòn"));

        public Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken) => Task.FromResult(Pickups.GetValueOrDefault(vendorId));

        public Task SavePickupAsync(GhnPickup pickup, CancellationToken cancellationToken)
        {
            Pickups[pickup.VendorId] = pickup;
            return Task.CompletedTask;
        }

        public Task<bool> DeletePickupAsync(int vendorId, CancellationToken cancellationToken) => Task.FromResult(Pickups.Remove(vendorId));

        public Task<IReadOnlyDictionary<int, GhnProductSize>> GetProductSizesAsync(IReadOnlyCollection<int> productIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, GhnProductSize>>(Sizes.Where(s => productIds.Contains(s.Key)).ToDictionary(s => s.Key, s => s.Value));

        public Task SaveShipmentAsync(GhnShipment shipment, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetShippingAdjustmentAsync(int shopOrderId, decimal amount, CancellationToken cancellationToken)
        {
            Adjustments[shopOrderId] = amount;
            return Task.CompletedTask;
        }

        public Task MarkCodTransferredAsync(int shopOrderId, DateTime transferredOnUtc, CancellationToken cancellationToken)
        {
            CodTransfers.TryAdd(shopOrderId, transferredOnUtc);
            return Task.CompletedTask;
        }
    }

    private static GhnOptions Configured(bool enabled = true, bool insure = false) =>
        new() { Enabled = enabled, Token = "t", ShopId = 1, ItemWeightGrams = 500, VndPerPrimaryUnit = 25_000m, InsureParcels = insure };

    private static GhnShippingService Service(FakeGhnClient client, GhnOptions settings, FakeCurrencyStore? currencies = null, FakeGhnStore? store = null) =>
        new(client, store ?? new FakeGhnStore(), null!, null!, new FakePrimaryCurrency(),
            new CurrencyService(currencies ?? new FakeCurrencyStore(), new RecordingAuditLog(), new TestClock()), new RecordingAuditLog(), new TestClock(),
            Options.Create(settings));

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
    public void DeliveredMovesTheOrderAndTheOptionsKnowWhenGhnIsSetUp()
    {
        Assert.Null(GhnRules.TrackingStatusOf("delivered"));
        Assert.Equal("Giao hàng thất bại", GhnRules.NoteOf("delivery_fail"));
        Assert.False(new GhnOptions { Enabled = true, Token = "t" }.IsConfigured);
        Assert.True(Configured().IsConfigured);
    }

    [Fact]
    public void GhnChargesByTheHeavierOfTheWeightAndTheVolume()
    {
        Assert.Equal(600, GhnRules.ChargeableWeight(new GhnParcel(500, 20, 15, 10)));
        Assert.Equal(12_000, GhnRules.ChargeableWeight(new GhnParcel(500, 50, 40, 30)));
        Assert.Equal(4_000, GhnRules.ChargeableWeight(new GhnParcel(4_000, 10, 10, 10)));
    }

    [Fact]
    public void TheDeclaredValueIsTheGoodsUpToGhnsCeilingAndNothingWithoutInsurance()
    {
        Assert.Equal(1_200_000, GhnRules.InsuranceOf(1_200_000, Configured(insure: true)));
        Assert.Equal(5_000_000, GhnRules.InsuranceOf(9_000_000, Configured(insure: true)));
        Assert.Equal(0, GhnRules.InsuranceOf(1_200_000, Configured(insure: false)));
    }

    [Fact]
    public void TheShopOwesTheRiseInFeeOnlyWhenGhnWeighedItHeavier()
    {
        Assert.Equal(0.40m, GhnRules.WeightAdjustment(600, 1500, 1.00m, 1.40m));
        // Same or lighter weight: a dearer fee is the platform's business.
        Assert.Equal(0m, GhnRules.WeightAdjustment(600, 600, 1.00m, 1.40m));
        Assert.Equal(0m, GhnRules.WeightAdjustment(600, 1500, 1.00m, 0.90m));
    }

    // ---- Quote ----

    [Fact]
    public async Task AQuoteGoesFromTheShopsPlaceToTheCustomersInTwoLevelUnitsAndConvertsGhnsVnd()
    {
        var client = new FakeGhnClient();

        var option = await Service(client, Configured()).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1), new GhnLineRef(2, 2)], 30m, CancellationToken.None);

        var sent = client.FeeRequests.Single();
        Assert.Equal((Hanoi, Saigon, 1500), (sent.From, sent.To, sent.Parcel.WeightGrams));
        // 3 units of 500 g: 20,000 + 15,000 VND = 35,000 VND, at 25,000 VND a dollar.
        Assert.Equal((GhnRules.RateId, 1.40m, false), (option!.RateId, option.Fee, option.IsFree));
    }

    [Fact]
    public async Task AQuoteCarriesGhnsExpectedDeliveryDaysWhenItGivesThem()
    {
        var option = await Service(new FakeGhnClient(), Configured()).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 30m, CancellationToken.None);
        Assert.Equal((new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc)),
            (option!.EstimatedFromUtc!.Value, option.EstimatedToUtc!.Value));

        var noDays = await Service(new FakeGhnClient { LeadtimeFails = true }, Configured()).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 30m, CancellationToken.None);
        Assert.Equal((1.00m, (DateTime?)null), (noDays!.Fee, noDays.EstimatedFromUtc));
    }

    [Fact]
    public async Task InsuredParcelsDeclareTheGoodsValue()
    {
        var client = new FakeGhnClient();

        // 30 dollars of goods are 750,000 VND declared; the fake charges 1% of it on top.
        var option = await Service(client, Configured(insure: true)).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 30m, CancellationToken.None);

        Assert.Equal(750_000, client.FeeRequests.Single().InsuranceVnd);
        Assert.Equal(1.30m, option!.Fee);
    }

    [Fact]
    public async Task AVndCurrencyGivesTheRate()
    {
        var currencies = new FakeCurrencyStore();
        currencies.Currencies.Add(new Currency { Id = 2, Code = "VND", Name = "Dong", Symbol = "₫", DecimalPlaces = 0, RateToPrimary = 35_000m, Published = true });

        var option = await Service(new FakeGhnClient(), Configured(), currencies).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 30m, CancellationToken.None);

        Assert.Equal(Math.Round(25_000m / 35_000m, 2), option!.Fee);
    }

    [Fact]
    public async Task AShopWithoutPickupOrAnAddressWithoutPlaceIsNotQuoted()
    {
        var client = new FakeGhnClient();

        // A shop that has not given a pickup address does not ship, and GHN is not even asked.
        Assert.Null(await Service(client, Configured()).QuoteAsync(PickupShop + 1, Saigon, [new GhnLineRef(1, 1)], 30m, CancellationToken.None));
        Assert.Null(await Service(client, Configured()).QuoteAsync(PickupShop, null, [new GhnLineRef(1, 1)], 30m, CancellationToken.None));
        Assert.Empty(client.FeeRequests);
    }

    [Fact]
    public async Task WhenGhnCannotPriceOrIsOffTheFallbackFeeIsCharged()
    {
        var client = new FakeGhnClient { FeeError = "Route not supported" };

        var refused = await Service(client, Configured()).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 30m, CancellationToken.None);
        // 30,000 VND at 25,000 VND a dollar.
        Assert.Equal((GhnRules.RateId, GhnRules.FallbackMethodName, 1.20m), (refused!.RateId, refused.Name, refused.Fee));

        // Without GHN at all every shop ships at the fallback fee, pickup or not, place or not.
        var off = await Service(new FakeGhnClient(), Configured(enabled: false)).QuoteAsync(PickupShop + 1, null, [new GhnLineRef(1, 1)], 30m, CancellationToken.None);
        Assert.Equal((GhnRules.FallbackMethodName, 1.20m), (off!.Name, off.Fee));
    }

    [Fact]
    public async Task OverTheShopsThresholdShippingIsFreeForTheCustomerAndTheShopTakesTheFeeOn()
    {
        var store = new FakeGhnStore();
        store.Pickups[PickupShop].FreeShippingOverSubtotal = 50m;

        var below = await Service(new FakeGhnClient(), Configured(), store: store).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 49.99m, CancellationToken.None);
        var over = await Service(new FakeGhnClient(), Configured(), store: store).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 1)], 50m, CancellationToken.None);

        // 1 unit: 20,000 + 5,000 VND = 1.00 dollar.
        Assert.Equal((1.00m, false, 0m), (below!.Fee, below.IsFree, below.ShopCost));
        Assert.Equal((0m, true, 1.00m), (over!.Fee, over.IsFree, over.ShopCost));
    }

    [Fact]
    public async Task OnlyAShopWithAPickupAddressCanSellWhileGhnIsOn()
    {
        Assert.True(await Service(new FakeGhnClient(), Configured()).CanShipAsync(PickupShop, CancellationToken.None));
        Assert.False(await Service(new FakeGhnClient(), Configured()).CanShipAsync(PickupShop + 1, CancellationToken.None));
        Assert.True(await Service(new FakeGhnClient(), Configured(enabled: false)).CanShipAsync(PickupShop + 1, CancellationToken.None));
    }

    // ---- The shipping quote offers GHN for an address with GHN's place ----

    private sealed class FakeGhn(ShippingOption? option, bool configured = true) : IGhnShippingService
    {
        public List<(GhnPlace? Place, int Units, decimal Subtotal)> Asked { get; } = [];
        public bool IsConfigured => configured;

        public Task<ShippingOption?> QuoteAsync(int vendorId, GhnPlace? destination, IReadOnlyList<GhnLineRef> lines, decimal subtotal, CancellationToken cancellationToken)
        {
            Asked.Add((destination, lines.Sum(l => l.Quantity), subtotal));
            return Task.FromResult(option);
        }

        public Task<bool> CanShipAsync(int vendorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int provinceId, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    private static async Task<CatalogResult<ShippingQuote>> QuoteFor(FakeGhn ghn, CustomerAddress address)
    {
        var addresses = new ShippingTests.StubAddresses();
        addresses.Addresses.Add(address);
        var cart = new ShippingTests.StubCart { View = new CartView("VND", [new CartShopGroup(5, "Shop", [Line(1, 5, 2), Line(2, 5, 1)], 30m)], 30m, 3, true) };
        return await new ShippingService(cart, addresses, ghn).QuoteAsync(Buyer, new ShippingQuoteRequest(address.Id), CancellationToken.None);
    }

    private static CustomerAddress Address(bool withPlace = true) => new()
    {
        Id = 9, CustomerId = Buyer, FirstName = "A", LastName = "B", Address1 = "1 Le Loi", City = "HCM", CountryCode = "VN", PhoneNumber = "0900000000",
        GhnProvinceId = withPlace ? Saigon.ProvinceId : null, GhnProvinceName = withPlace ? "Hồ Chí Minh" : null,
        GhnWardId = withPlace ? Saigon.WardId : null, GhnWardName = withPlace ? "Phường Sài Gòn" : null
    };

    [Fact]
    public async Task EveryShopOfTheCartGetsItsGhnOption()
    {
        var ghn = new FakeGhn(new ShippingOption(GhnRules.RateId, GhnRules.MethodName, 1.4m, false, null, null));

        var quote = (await QuoteFor(ghn, Address())).Value!;

        Assert.Equal([((GhnPlace?)Saigon, 3, 30m)], ghn.Asked);
        Assert.Equal([GhnRules.RateId], quote.Shops[0].Options.Select(o => o.RateId));
        Assert.True(quote.CanShipAll);
        Assert.Equal(1.4m, quote.ShippingTotal);
    }

    [Fact]
    public async Task AnAddressWithoutGhnsPlaceHasToBeCompletedFirst()
    {
        var ghn = new FakeGhn(new ShippingOption(GhnRules.RateId, GhnRules.MethodName, 1.4m, false, null, null));

        var result = await QuoteFor(ghn, Address(withPlace: false));

        Assert.Equal(ShippingErrors.AreaMissing, result.ErrorCode);
        Assert.Empty(ghn.Asked);
        // Without GHN the place does not matter: every shop gets the fallback.
        Assert.True((await QuoteFor(new FakeGhn(new ShippingOption(GhnRules.RateId, GhnRules.FallbackMethodName, 1.2m, false, null, null), configured: false),
            Address(withPlace: false))).Succeeded);
    }

    [Fact]
    public async Task AShopThatCannotShipBlocksTheQuote()
    {
        var quote = (await QuoteFor(new FakeGhn(null), Address())).Value!;

        Assert.False(quote.Shops[0].CanShip);
        Assert.False(quote.CanShipAll);
        Assert.Null(quote.ShippingTotal);
    }

    // ---- Webhook ----

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
        public FakeGhnStore Store { get; } = new();
        public FakeGhnClient Client { get; } = new();

        public GhnShippingService Create(string secret = "s3cret") =>
            new(Client, Store, Orders, Tracking, new FakePrimaryCurrency(),
                new CurrencyService(new FakeCurrencyStore(), new RecordingAuditLog(), new TestClock()), new RecordingAuditLog(), new TestClock(),
                Options.Create(new GhnOptions { Enabled = true, Token = "t", ShopId = 229760, WebhookSecret = secret, VndPerPrimaryUnit = 25_000m, InsureParcels = false }));
    }

    private static GhnWebhook Hook(
        string status, string type = "switch_status", int shop = 229760, string code = "L8RLBD", string time = "2026-01-02T03:04:05Z",
        int weight = 500, int converted = 600, long totalFee = 0, string? codTransfer = null) =>
        new(shop, type, code, status, time, null, null, weight, converted, totalFee, codTransfer);

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
        Assert.Equal((TrackingRules.PickedUp, "Đã lấy hàng", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)),
            (line.Command.Status, line.Command.Note, line.Command.OccurredOnUtc));
        Assert.Empty(f.Orders.DeliveredByCarrier);
        // GHN weighed it as declared (600 g): the shop owes nothing.
        Assert.Empty(f.Store.Adjustments);
    }

    [Fact]
    public async Task DeliveredMarksTheShopOrderDeliveredByTheCarrier()
    {
        var f = new WebhookFixture();

        await f.Create().HandleWebhookAsync(Hook("delivered"), CancellationToken.None);

        Assert.Equal([(9, "Giao hàng thành công")], f.Orders.DeliveredByCarrier);
        // The journey ends with the delivery, written before the order leaves "shipped".
        var line = Assert.Single(f.Tracking.Added);
        Assert.Equal((TrackingRules.Info, "Giao hàng thành công"), (line.Command.Status, line.Command.Note));
    }

    [Fact]
    public async Task AParcelGhnWeighedHeavierMakesTheShopOweTheRiseInFee()
    {
        var f = new WebhookFixture();

        // GHN found 1,500 g instead of the 600 g declared and sent no fee: GHN is asked again, 20,000 + 15,000 VND = 1.40 against 1.00.
        await f.Create().HandleWebhookAsync(Hook("", type: "update_weight", weight: 1500, converted: 900), CancellationToken.None);

        Assert.Equal(1500, f.Client.FeeRequests.Single().Parcel.WeightGrams);
        Assert.Equal((Hanoi, Saigon), (f.Client.FeeRequests[0].From, f.Client.FeeRequests[0].To));
        Assert.Equal(0.40m, f.Store.Adjustments[9]);
        // Not a status change: no tracking line.
        Assert.Empty(f.Tracking.Added);
    }

    [Fact]
    public async Task AFeeGhnSendsIsTakenAsItIs()
    {
        var f = new WebhookFixture();

        await f.Create().HandleWebhookAsync(Hook("", type: "update_weight", weight: 2000, converted: 600, totalFee: 50_000), CancellationToken.None);

        Assert.Empty(f.Client.FeeRequests);
        Assert.Equal(1.00m, f.Store.Adjustments[9]);
    }

    [Fact]
    public async Task ACodTransferIsRecordedOnce()
    {
        var f = new WebhookFixture();

        await f.Create().HandleWebhookAsync(Hook("", type: "cod", codTransfer: "2026-01-05T08:00:00Z"), CancellationToken.None);
        await f.Create().HandleWebhookAsync(Hook("", type: "cod", codTransfer: "2026-01-06T08:00:00Z"), CancellationToken.None);

        Assert.Equal(new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc), f.Store.CodTransfers[9]);
    }

    [Fact]
    public async Task OtherShopsAndOrdersAreLeftAlone()
    {
        var f = new WebhookFixture();

        Assert.Equal(GhnWebhookOutcome.Handled, await f.Create().HandleWebhookAsync(Hook("picked", type: "update_cod"), CancellationToken.None));
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

        var bad = await service.SavePickupAsync(7, new SaveGhnPickupCommand(" ", "123", "", 0, null, 0, null), 1, CancellationToken.None);
        Assert.Equal(["address", "contactName", "phone", "wardId"], bad.Errors.Keys.Order());

        var saved = await service.SavePickupAsync(7, new SaveGhnPickupCommand(
            " Meo ", "090 123 4567", " 12 Nguyễn Huệ ", Saigon.ProvinceId, "Hồ Chí Minh", Saigon.WardId, "Phường Sài Gòn"), 1, CancellationToken.None);
        Assert.True(saved.Succeeded);
        Assert.Equal(("Meo", "0901234567", "12 Nguyễn Huệ", Saigon), (store.Pickups[7].ContactName, store.Pickups[7].Phone, store.Pickups[7].Address, store.Pickups[7].Place));
        Assert.Equal(new GhnSender("Meo", "0901234567", "12 Nguyễn Huệ", Saigon.ProvinceId, "Hồ Chí Minh", Saigon.WardId, "Phường Sài Gòn"), store.Pickups[7].ToSender());

        await service.DeletePickupAsync(7, 1, CancellationToken.None);
        Assert.False(store.Pickups.ContainsKey(7));
    }

    // ---- A shop needs its pickup address to add products ----

    private static ProductService Products(FakeVendorStore vendors, GhnOptions? settings = null)
    {
        var products = new ProductOwnershipTests.FakeProductStore();
        return new ProductService(products, new FakeInventoryStore(products), new FakePrimaryCurrency(), new ProductOwnershipTests.UnusedCategoryStore(),
            new ProductOwnershipTests.UnusedManufacturerStore(), vendors, new ProductOwnershipTests.NoMembers(), new FakeMediaStore(),
            new ProductOwnershipTests.FakeTaxonomy(), new RecordingAuditLog(), new RecordingEmailSender(), TestOptions.Email(false),
            NullLog<ProductService>.Instance, new TestClock(), settings is null ? null : new FakeGhnStore(), settings is null ? null : Options.Create(settings));
    }

    [Fact]
    public async Task AShopWithoutAPickupAddressCannotAddProductsWhileGhnIsOn()
    {
        var vendors = new FakeVendorStore();
        vendors.Vendors.Add(new Core.Vendors.Vendor { Id = PickupShop, Name = "With pickup", Active = true });
        vendors.Vendors.Add(new Core.Vendors.Vendor { Id = PickupShop + 1, Name = "Without", Active = true });
        var command = new SaveVendorProductCommand("Mug", null, null, 10m, 0, 1, [1], [], WeightGrams: 500);

        Assert.True((await Products(vendors, Configured()).CreateForVendorAsync(PickupShop, command, 10, CancellationToken.None)).Succeeded);
        Assert.Equal(GhnErrors.NoPickup, (await Products(vendors, Configured()).CreateForVendorAsync(PickupShop + 1, command, 10, CancellationToken.None)).ErrorCode);
        // Without GHN there is nothing to give, so nothing is asked.
        Assert.True((await Products(vendors, Configured(enabled: false)).CreateForVendorAsync(PickupShop + 1, command, 10, CancellationToken.None)).Succeeded);
    }

    // ---- The parcel follows the products' weight and size ----

    [Fact]
    public void AParcelAddsTheWeightsAndStacksTheSizedUnits()
    {
        var settings = Configured();

        // No product says anything: 500 g a unit and the default box.
        Assert.Equal(new GhnParcel(1500, 20, 15, 10), GhnRules.Parcel([(3, null)], settings));

        // A 2 kg speaker 30x20x15 twice, and a notebook of 300 g without a size: weights add up, the speakers stack.
        var parcel = GhnRules.Parcel([(2, new GhnProductSize(2000, 30, 20, 15)), (1, new GhnProductSize(300, null, null, null))], settings);
        Assert.Equal(new GhnParcel(4300, 30, 20, 30), parcel);

        // A tall stack stops at GHN's 200 cm.
        Assert.Equal(200, GhnRules.Parcel([(30, new GhnProductSize(100, 10, 10, 10))], settings).HeightCm);
    }

    [Fact]
    public async Task AQuoteSendsGhnTheParcelOfTheProductsWeightAndSize()
    {
        var store = new FakeGhnStore();
        store.Sizes[1] = new GhnProductSize(2000, 30, 20, 15);
        var client = new FakeGhnClient();

        await Service(client, Configured(), store: store).QuoteAsync(PickupShop, Saigon, [new GhnLineRef(1, 2)], 30m, CancellationToken.None);

        Assert.Equal(new GhnParcel(4000, 30, 20, 30), client.FeeRequests.Single().Parcel);
    }

    [Fact]
    public async Task ASellerGivesThePackedWeightAndOptionallyTheWholeSize()
    {
        var vendors = new FakeVendorStore();
        vendors.Vendors.Add(new Core.Vendors.Vendor { Id = PickupShop, Name = "Shop", Active = true });
        var service = Products(vendors);
        Task<CatalogResult<Product>> Save(int? weight, int? l = null, int? w = null, int? h = null) => service.CreateForVendorAsync(
            PickupShop, new SaveVendorProductCommand("Mug", null, null, 10m, 0, 1, [1], [], WeightGrams: weight, LengthCm: l, WidthCm: w, HeightCm: h),
            10, CancellationToken.None);

        Assert.Contains("weightGrams", (await Save(null)).Errors.Keys);
        Assert.Contains("weightGrams", (await Save(50_001)).Errors.Keys);
        Assert.Contains("dimensions", (await Save(500, 30, null, 10)).Errors.Keys);
        Assert.Contains("dimensions", (await Save(500, 30, 20, 201)).Errors.Keys);
        var saved = await Save(500, 30, 20, 10);
        Assert.Equal((500, 30, 20, 10), (saved.Value!.WeightGrams, saved.Value.LengthCm, saved.Value.WidthCm, saved.Value.HeightCm));
        Assert.True((await Save(800)).Succeeded);
    }
}
