using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Services.Directory;
using Nomori.Marketplace.Services.Shipping;
using static Nomori.Marketplace.Services.Tests.DirectoryTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class ShippingTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Buyer = 100;
    private const int Seller = 10;

    private sealed class FakeShippingStore : IShippingStore
    {
        private int nextId = 1;

        public List<ShippingRate> Rates { get; } = [];

        public Task<IReadOnlyList<ShippingRate>> GetRatesAsync(int vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShippingRate>>(Rates.Where(r => r.VendorId == vendorId).ToList());

        public Task<ShippingRate?> GetRateAsync(int vendorId, int id, CancellationToken cancellationToken) =>
            Task.FromResult(Rates.FirstOrDefault(r => r.VendorId == vendorId && r.Id == id));

        public Task<IReadOnlyList<ShippingRate>> GetPublishedRatesAsync(IReadOnlyCollection<int> vendorIds, string countryCode, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ShippingRate>>(Rates.Where(r => r.Published && r.CountryCode == countryCode && vendorIds.Contains(r.VendorId)).ToList());

        public Task<int> CountRatesAsync(int vendorId, CancellationToken cancellationToken) =>
            Task.FromResult(Rates.Count(r => r.VendorId == vendorId));

        public Task<int> InsertAsync(ShippingRate rate, CancellationToken cancellationToken)
        {
            rate.Id = nextId++;
            Rates.Add(rate);
            return Task.FromResult(rate.Id);
        }

        public Task UpdateAsync(ShippingRate rate, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeleteAsync(int vendorId, int id, CancellationToken cancellationToken) =>
            Task.FromResult(Rates.RemoveAll(r => r.VendorId == vendorId && r.Id == id) > 0);
    }

    private sealed class StubCart : ICartService
    {
        public CartView View { get; set; } = new("USD", [], 0, 0, false);

        public Task<CartView> GetAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(View);
        public Task<int> CountAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> AddAsync(int customerId, AddToCartCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> SetQuantityAsync(int customerId, int lineId, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> RemoveAsync(int customerId, int lineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> ClearAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> AcceptPricesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class StubAddresses : ICustomerAccountDataService
    {
        public List<CustomerAddress> Addresses { get; } = [];

        public Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerAddress>>(Addresses.Where(a => a.CustomerId == customerId).ToList());

        public Task<(CustomerAddress? Address, IReadOnlyDictionary<string, string[]> Errors)> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string[]>> SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EmailChangeRequestResult> RequestEmailChangeAsync(int customerId, string newEmail, string currentPassword, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ConfirmEmailChangeAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public FakeShippingStore Store { get; } = new();
        public FakeDirectoryStore Directory { get; } = new();
        public StubCart Cart { get; } = new();
        public StubAddresses Addresses { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public int UsStateId { get; }

        public Fixture()
        {
            Directory.Countries.Add(new Country { Id = 1, Code = "US", Name = "United States", Published = true, AllowsBilling = true, AllowsShipping = true });
            Directory.Countries.Add(new Country { Id = 2, Code = "FR", Name = "France", Published = true, AllowsBilling = true, AllowsShipping = true });
            Directory.Countries.Add(new Country { Id = 3, Code = "KP", Name = "Closed", Published = true, AllowsBilling = true, AllowsShipping = false });
            Directory.States.Add(new StateProvince { Id = 1, CountryId = 1, Code = "CA", Name = "California", Published = true });
            Directory.States.Add(new StateProvince { Id = 2, CountryId = 1, Code = "NY", Name = "New York", Published = true });
            UsStateId = 1;
        }

        public ShippingService Create() => new(
            Store, new DirectoryService(Directory, Audit, new TestClock()), Cart, Addresses, new FakePrimaryCurrency(), Audit, new TestClock());

        public ShippingRate Seed(int vendor = Shop, string country = "US", int? state = null, decimal fee = 5, decimal? freeOver = null,
            string name = "Standard", bool published = true)
        {
            var rate = new ShippingRate { VendorId = vendor, Name = name, CountryCode = country, StateProvinceId = state, Fee = fee, FreeOverSubtotal = freeOver, Published = published };
            Store.InsertAsync(rate, CancellationToken.None).GetAwaiter().GetResult();
            return rate;
        }

        /// <summary>A cart with one group per (shop, subtotal).</summary>
        public void CartOf(params (int Vendor, decimal Subtotal)[] shops) =>
            Cart.View = new CartView("USD",
                shops.Select(s => new CartShopGroup(s.Vendor, "Shop " + s.Vendor, [], s.Subtotal)).ToList(),
                shops.Sum(s => s.Subtotal), shops.Length, true);
    }

    private static SaveShippingRateCommand Command(
        string? name = "Standard", string? country = "US", int? state = null, decimal fee = 5, decimal? freeOver = null,
        int? minDays = null, int? maxDays = null, bool published = true) =>
        new(name, country, state, fee, freeOver, minDays, maxDays, published, 0);

    private static Task<CatalogResult<ShippingQuote>> Quote(Fixture f, string? country = "US", int? state = 1, int? addressId = null) =>
        f.Create().QuoteAsync(Buyer, new ShippingQuoteRequest(addressId, country, state), CancellationToken.None);

    // ---- Pure rules ----

    private static ShippingRate Rate(string country = "US", int? state = null, bool published = true, decimal fee = 5, decimal? freeOver = null, string name = "Standard", int id = 1) =>
        new() { Id = id, Name = name, CountryCode = country, StateProvinceId = state, Published = published, Fee = fee, FreeOverSubtotal = freeOver };

    [Theory]
    [InlineData("US", null, "US", 7, true)]
    [InlineData("US", null, "us", null, true)]
    [InlineData("US", null, "FR", null, false)]
    [InlineData("US", 1, "US", 1, true)]
    [InlineData("US", 1, "US", 2, false)]
    [InlineData("US", 1, "US", null, false)]
    public void ARateCoversItsCountryOrItsState(string rateCountry, int? rateState, string country, int? state, bool expected) =>
        Assert.Equal(expected, ShippingRules.Matches(Rate(rateCountry, rateState), country, state));

    [Fact]
    public void AnUnpublishedRateNeverMatches() =>
        Assert.False(ShippingRules.Matches(Rate(published: false), "US", null));

    [Theory]
    [InlineData(49.99, 5)]
    [InlineData(50, 0)]
    [InlineData(80, 0)]
    public void TheThresholdItselfShipsFree(double subtotal, double expected) =>
        Assert.Equal((decimal)expected, ShippingRules.FeeFor(Rate(freeOver: 50), (decimal)subtotal));

    [Fact]
    public void ARateWithoutAThresholdAlwaysCharges() =>
        Assert.Equal(5m, ShippingRules.FeeFor(Rate(), 1_000_000m));

    [Fact]
    public void OptionsAreSortedByFeeThenName()
    {
        var options = ShippingRules.Options(
            [Rate(fee: 9, name: "Express", id: 1), Rate(fee: 5, name: "Zeta", id: 2), Rate(fee: 5, name: "Alpha", id: 3), Rate("FR", id: 4)],
            "US", null, 10);

        Assert.Equal(["Alpha", "Zeta", "Express"], options.Select(o => o.Name));
    }

    [Fact]
    public void TheTotalIsTheSumOfTheCheapestAndNullWhenAShopCannotShip()
    {
        ShippingShopQuote Shop(params decimal[] fees) =>
            new(1, "s", 10, fees.Select((f, i) => new ShippingOption(i, "o", f, f == 0, null, null)).ToList(), fees.Length > 0);

        Assert.Equal(8m, ShippingRules.ShippingTotal([Shop(3, 9), Shop(5)]));
        Assert.Null(ShippingRules.ShippingTotal([Shop(3), Shop()]));
        Assert.Null(ShippingRules.ShippingTotal([]));
    }

    // ---- Rates ----

    [Fact]
    public async Task ASeedRateIsCreatedAndAudited()
    {
        var f = new Fixture();

        var result = await f.Create().CreateRateAsync(Shop, Command(" Standard ", "us", fee: 4.5m, freeOver: 50, minDays: 3, maxDays: 5), Seller, CancellationToken.None);

        Assert.True(result.Succeeded);
        var rate = Assert.Single(f.Store.Rates);
        Assert.Equal((Shop, "Standard", "US", 4.5m, 50m, 3, 5), (rate.VendorId, rate.Name, rate.CountryCode, rate.Fee, rate.FreeOverSubtotal, rate.MinDays, rate.MaxDays));
        Assert.Contains("shipping.rate_created", f.Audit.Events);
    }

    [Fact]
    public async Task ARateCanCoverOneStateOfItsCountry()
    {
        var f = new Fixture();

        Assert.True((await f.Create().CreateRateAsync(Shop, Command(state: f.UsStateId), Seller, CancellationToken.None)).Succeeded);

        var other = await f.Create().CreateRateAsync(Shop, Command(country: "FR", state: f.UsStateId), Seller, CancellationToken.None);
        Assert.Contains("stateProvinceId", other.Errors.Keys);
    }

    [Theory]
    [InlineData("", "US", 5, null, null, null, "name")]
    [InlineData("x", "ZZ", 5, null, null, null, "countryCode")]
    [InlineData("x", "KP", 5, null, null, null, "countryCode")]
    [InlineData("x", "US", -1, null, null, null, "fee")]
    [InlineData("x", "US", 5.123, null, null, null, "fee")]
    [InlineData("x", "US", 5, 0.0, null, null, "freeOverSubtotal")]
    [InlineData("x", "US", 5, 10.005, null, null, "freeOverSubtotal")]
    [InlineData("x", "US", 5, null, -1, null, "minDays")]
    [InlineData("x", "US", 5, null, null, 366, "maxDays")]
    [InlineData("x", "US", 5, null, 7, 3, "minDays")]
    public async Task InvalidRatesAreRefusedWithTheFieldName(
        string name, string country, double fee, double? freeOver, int? minDays, int? maxDays, string field)
    {
        var f = new Fixture();

        var result = await f.Create().CreateRateAsync(
            Shop, Command(name, country, fee: (decimal)fee, freeOver: (decimal?)freeOver, minDays: minDays, maxDays: maxDays), Seller, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(f.Store.Rates);
    }

    [Fact]
    public async Task ANameTooLongIsRefused()
    {
        var f = new Fixture();

        var result = await f.Create().CreateRateAsync(Shop, Command(new string('x', 101)), Seller, CancellationToken.None);

        Assert.Contains("name", result.Errors.Keys);
    }

    [Fact]
    public async Task AShopCanHaveAtMostFiftyRates()
    {
        var f = new Fixture();
        for (var i = 0; i < ShippingLimits.MaxRatesPerShop; i++) f.Seed();

        var result = await f.Create().CreateRateAsync(Shop, Command(), Seller, CancellationToken.None);

        Assert.Equal(ShippingErrors.RateLimit, result.ErrorCode);
        // Another shop has its own count.
        Assert.True((await f.Create().CreateRateAsync(OtherShop, Command(), Seller, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ARateOfAnotherShopIsNotFound()
    {
        var f = new Fixture();
        var theirs = f.Seed(OtherShop);

        var update = await f.Create().UpdateRateAsync(Shop, theirs.Id, Command(fee: 1), Seller, CancellationToken.None);
        var delete = await f.Create().DeleteRateAsync(Shop, theirs.Id, Seller, CancellationToken.None);

        Assert.Equal(CatalogErrors.NotFound, update.ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, delete.ErrorCode);
        Assert.Equal(5m, theirs.Fee);
        Assert.Single(f.Store.Rates);
    }

    [Fact]
    public async Task UpdatingAndDeletingAreAudited()
    {
        var f = new Fixture();
        var rate = f.Seed();

        var update = await f.Create().UpdateRateAsync(Shop, rate.Id, Command(fee: 8, published: false), Seller, CancellationToken.None);
        var delete = await f.Create().DeleteRateAsync(Shop, rate.Id, Seller, CancellationToken.None);

        Assert.True(update.Succeeded);
        Assert.Equal((8m, false), (rate.Fee, rate.Published));
        Assert.True(delete.Succeeded);
        Assert.Empty(f.Store.Rates);
        Assert.Equal(["shipping.rate_updated", "shipping.rate_deleted"], f.Audit.Events);
    }

    [Fact]
    public async Task FeesFollowTheDecimalsOfThePrimaryCurrency()
    {
        var f = new Fixture();
        var zeroDecimals = new ShippingService(f.Store, new DirectoryService(f.Directory, f.Audit, new TestClock()), f.Cart, f.Addresses,
            new FakePrimaryCurrency(0), f.Audit, new TestClock());

        var result = await zeroDecimals.CreateRateAsync(Shop, Command(fee: 4.5m), Seller, CancellationToken.None);

        Assert.Contains("fee", result.Errors.Keys);
    }

    // ---- Quote ----

    [Fact]
    public async Task EachShopInTheCartGetsItsOwnOptions()
    {
        var f = new Fixture();
        f.Seed(Shop, fee: 5, name: "Standard");
        f.Seed(Shop, fee: 12, name: "Express");
        f.Seed(OtherShop, fee: 3);
        f.CartOf((Shop, 20), (OtherShop, 8));

        var quote = (await Quote(f)).Value!;

        Assert.True(quote.CanShipAll);
        Assert.Equal(["Standard", "Express"], quote.Shops[0].Options.Select(o => o.Name));
        Assert.Equal([3m], quote.Shops[1].Options.Select(o => o.Fee));
        Assert.Equal(8m, quote.ShippingTotal);
        Assert.Equal("USD", quote.CurrencyCode);
    }

    [Fact]
    public async Task AShopWithoutARateCannotShipThere()
    {
        var f = new Fixture();
        f.Seed(Shop);
        f.CartOf((Shop, 20), (OtherShop, 8));

        var quote = (await Quote(f)).Value!;

        Assert.False(quote.CanShipAll);
        Assert.True(quote.Shops[0].CanShip);
        Assert.False(quote.Shops[1].CanShip);
        Assert.Null(quote.ShippingTotal);
    }

    [Fact]
    public async Task FreeShippingFollowsTheSubtotalOfTheShop()
    {
        var f = new Fixture();
        f.Seed(Shop, fee: 5, freeOver: 50);
        f.Seed(OtherShop, fee: 4, freeOver: 50);
        f.CartOf((Shop, 50), (OtherShop, 49.99m));

        var quote = (await Quote(f)).Value!;

        Assert.True(quote.Shops[0].Options[0].IsFree);
        Assert.False(quote.Shops[1].Options[0].IsFree);
        Assert.Equal(4m, quote.ShippingTotal);
    }

    [Fact]
    public async Task OnlyRatesOfTheDestinationCountApply()
    {
        var f = new Fixture();
        f.Seed(Shop, "US", state: null, fee: 5, name: "Country");
        f.Seed(Shop, "US", state: f.UsStateId, fee: 2, name: "California");
        f.Seed(Shop, "FR", fee: 9, name: "France");
        f.Seed(Shop, "US", published: false, fee: 1, name: "Hidden");
        f.CartOf((Shop, 10));

        var california = (await Quote(f, "US", f.UsStateId)).Value!;
        var newYork = (await Quote(f, "US", 2)).Value!;
        var france = (await Quote(f, "FR", null)).Value!;

        Assert.Equal(["California", "Country"], california.Shops[0].Options.Select(o => o.Name));
        Assert.Equal(["Country"], newYork.Shops[0].Options.Select(o => o.Name));
        Assert.Equal(["France"], france.Shops[0].Options.Select(o => o.Name));
    }

    [Fact]
    public async Task AnEmptyCartHasNothingToShip()
    {
        var f = new Fixture();

        var quote = (await Quote(f)).Value!;

        Assert.Empty(quote.Shops);
        Assert.False(quote.CanShipAll);
        Assert.Null(quote.ShippingTotal);
    }

    [Theory]
    [InlineData("ZZ", null, "countryCode")]
    [InlineData("KP", null, "countryCode")]
    [InlineData("", null, "countryCode")]
    [InlineData("US", null, "stateProvinceId")]
    [InlineData("US", 99, "stateProvinceId")]
    [InlineData("FR", 1, "stateProvinceId")]
    public async Task ABadDestinationIsRefusedWithTheFieldName(string country, int? state, string field)
    {
        var f = new Fixture();
        f.CartOf((Shop, 10));

        var result = await Quote(f, country, state);

        Assert.False(result.Succeeded);
        Assert.Contains(field, result.Errors.Keys);
    }

    [Fact]
    public async Task ASavedAddressIsUsedForTheDestination()
    {
        var f = new Fixture();
        f.Seed(Shop, "US", state: f.UsStateId, fee: 2);
        f.Addresses.Addresses.Add(new CustomerAddress { Id = 7, CustomerId = Buyer, CountryCode = "US", StateProvinceId = f.UsStateId });
        f.CartOf((Shop, 10));

        var quote = (await Quote(f, country: null, addressId: 7)).Value!;

        Assert.Equal(("US", f.UsStateId, 2m), (quote.CountryCode, quote.StateProvinceId, quote.ShippingTotal));
    }

    [Fact]
    public async Task AnotherCustomersAddressIsNotFound()
    {
        var f = new Fixture();
        f.Addresses.Addresses.Add(new CustomerAddress { Id = 7, CustomerId = Buyer + 1, CountryCode = "US", StateProvinceId = f.UsStateId });
        f.CartOf((Shop, 10));

        var result = await Quote(f, country: null, addressId: 7);

        Assert.Equal(CatalogErrors.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task AnAddressInAClosedCountryCannotBeShippedTo()
    {
        var f = new Fixture();
        f.Addresses.Addresses.Add(new CustomerAddress { Id = 7, CustomerId = Buyer, CountryCode = "KP" });
        f.CartOf((Shop, 10));

        var result = await Quote(f, country: null, addressId: 7);

        Assert.Contains("addressId", result.Errors.Keys);
    }

    [Fact]
    public async Task ClosingACountryForShippingHidesItsRates()
    {
        var f = new Fixture();
        f.Seed(Shop, "FR");
        f.CartOf((Shop, 10));
        Assert.True((await Quote(f, "FR", null)).Value!.CanShipAll);

        f.Directory.Countries.Single(c => c.Code == "FR").AllowsShipping = false;

        Assert.Contains("countryCode", (await Quote(f, "FR", null)).Errors.Keys);
        Assert.Single(f.Store.Rates);
    }

    [Fact]
    public async Task FreeShippingCountsOnlyTheLinesBeingBought()
    {
        var f = new Fixture();
        f.Seed(Shop, fee: 5, freeOver: 50);
        static CartLineView Line(int id, decimal total) =>
            new(id, id, "P" + id, Shop, "Shop", 0, null, null, 1, total, null, total, PriceRule.Base, null, null, []);
        // 40 + 30 in the cart reaches the free threshold; only the 40 line is being bought.
        f.Cart.View = new CartView("USD", [new CartShopGroup(Shop, "Shop", [Line(1, 40m), Line(2, 30m)], 70m)], 70m, 2, true);

        var whole = (await f.Create().QuoteAsync(Buyer, new ShippingQuoteRequest(null, "US", 1), CancellationToken.None)).Value!;
        var chosen = (await f.Create().QuoteAsync(Buyer, new ShippingQuoteRequest(null, "US", 1, [1]), CancellationToken.None)).Value!;

        Assert.Equal(0m, whole.Shops.Single().Options.Single().Fee);
        Assert.Equal((40m, 5m), (chosen.Shops.Single().Subtotal, chosen.Shops.Single().Options.Single().Fee));
    }
}
