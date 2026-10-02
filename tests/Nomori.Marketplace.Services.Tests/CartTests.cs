using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Cart;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class CartTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Buyer = 100;
    private const int OtherBuyer = 101;
    private const int Seller = 10;

    // The test clock is 2026-01-01 00:00 UTC.
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class FakeCartStore : ICartStore
    {
        private int nextId = 1;

        public List<CartLine> Lines { get; } = [];

        /// <summary>Runs just before an insert is checked, so a test can play another request that got there first.</summary>
        public Action<CartLine>? BeforeInsert { get; set; }

        public Task<IReadOnlyList<CartLine>> GetLinesAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartLine>>(Lines.Where(l => l.CustomerId == customerId).ToList());

        public Task<CartLine?> GetLineAsync(int customerId, int lineId, CancellationToken cancellationToken) =>
            Task.FromResult(Lines.FirstOrDefault(l => l.CustomerId == customerId && l.Id == lineId));

        public Task<CartLine?> FindAsync(int customerId, int productId, string valueIds, CancellationToken cancellationToken) =>
            Task.FromResult(Lines.FirstOrDefault(l => l.CustomerId == customerId && l.ProductId == productId && l.ValueIds == valueIds));

        public Task<bool> InsertAsync(CartLine line, CancellationToken cancellationToken)
        {
            BeforeInsert?.Invoke(line);
            if (Lines.Any(l => l.CustomerId == line.CustomerId && l.ProductId == line.ProductId && l.ValueIds == line.ValueIds))
                return Task.FromResult(false);
            line.Id = nextId++;
            Lines.Add(line);
            return Task.FromResult(true);
        }

        public Task UpdateLineAsync(int lineId, int quantity, decimal addedUnitPrice, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var line = Lines.Single(l => l.Id == lineId);
            (line.Quantity, line.AddedUnitPrice, line.UpdatedOnUtc) = (quantity, addedUnitPrice, nowUtc);
            return Task.CompletedTask;
        }

        public Task SetAddedUnitPriceAsync(int lineId, decimal addedUnitPrice, CancellationToken cancellationToken)
        {
            Lines.Single(l => l.Id == lineId).AddedUnitPrice = addedUnitPrice;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(int customerId, int lineId, CancellationToken cancellationToken) =>
            Task.FromResult(Lines.RemoveAll(l => l.CustomerId == customerId && l.Id == lineId) > 0);

        public Task ClearAsync(int customerId, CancellationToken cancellationToken)
        {
            Lines.RemoveAll(l => l.CustomerId == customerId);
            return Task.CompletedTask;
        }

        public Task<int> CountUnitsAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Lines.Where(l => l.CustomerId == customerId).Sum(l => l.Quantity));
    }

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeAttributeStore Attributes { get; } = new();
        public FakeInventoryStore Inventory { get; }
        public FakeCartStore Carts { get; } = new();
        public NoMembers Members { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public TestClock Clock { get; } = new();

        public Fixture()
        {
            Inventory = new FakeInventoryStore(Products, Attributes);
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
            Attributes.Attributes.Add(new ProductAttributeSpec { Id = 1, Name = "Color" });
            Attributes.Attributes.Add(new ProductAttributeSpec { Id = 2, Name = "Size" });
        }

        public CartService Create()
        {
            var inventory = new InventoryService(Inventory, Products, Vendors, Attributes, Audit, Clock);
            var prices = new PriceCalculationService(Products, Attributes, new FakePrimaryCurrency(), Clock);
            return new CartService(Carts, Products, Attributes, Members, prices, inventory, new FakePrimaryCurrency(), Clock);
        }

        public ProductService CreateProducts() => new(
            Products, Inventory, new FakePrimaryCurrency(), new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, Members, new FakeMediaStore(),
            new FakeTaxonomy(), Audit, new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, Clock);

        /// <summary>A product a customer can see and buy.</summary>
        public async Task<int> LiveAsync(string name = "Mug", decimal price = 10, int stock = 10, int vendorId = Shop)
        {
            var result = await CreateProducts().CreateForVendorAsync(vendorId,
                new SaveVendorProductCommand(name, null, null, price, 0, stock, [1], []), Seller, CancellationToken.None);
            var product = result.Value!;
            product.Status = ProductStatus.Live;
            product.VendorActive = true;
            product.VendorName = vendorId == Shop ? "Shop" : "Other";
            return product.Id;
        }

        public Product Product(int id) => Products.Products.Single(p => p.Id == id);

        public async Task<ProductAttributeDetail> VariantsAsync(int productId, SaveVariantsCommand command)
        {
            var details = new VendorProductDetailsService(Products, Vendors, Attributes, new ProductAttributeService(Attributes), new FakeSpecificationStore(),
                Inventory, new FakePrimaryCurrency(), Audit, Clock);
            return (await details.SetVariantsAsync(Shop, productId, command, Seller, CancellationToken.None)).Value!;
        }
    }

    private static Task<CatalogResult<CartView>> Add(Fixture f, int productId, int quantity = 1, int customer = Buyer, params int[] valueIds) =>
        f.Create().AddAsync(customer, new AddToCartCommand(productId, quantity, valueIds), CancellationToken.None);

    private static CartLineView OnlyLine(CartView view) => view.Groups.Single().Lines.Single();

    // ---- Pure rules ----

    [Fact]
    public void ValueKeysAreSortedDistinctAndRoundTrip()
    {
        Assert.Equal("3,7,9", CartRules.ValueKey([9, 3, 7, 3]));
        Assert.Equal(string.Empty, CartRules.ValueKey(null));
        Assert.Equal(string.Empty, CartRules.ValueKey([]));
        Assert.Equal([3, 7, 9], CartRules.ParseValueKey("3,7,9"));
        Assert.Empty(CartRules.ParseValueKey(string.Empty));
    }

    [Theory]
    [InlineData(false, true, true, 5, 1, 10, 10, "unavailable")]
    [InlineData(true, false, true, 5, 1, 10, 10, "variant_unavailable")]
    [InlineData(true, true, true, 0, 1, 10, 10, "out_of_stock")]
    [InlineData(true, true, true, 2, 3, 10, 10, "insufficient_stock")]
    [InlineData(true, true, true, 3, 3, 10, 10, "")]
    [InlineData(true, true, false, 0, 3, 10, 10, "")]
    [InlineData(true, true, true, 5, 1, 12, 10, "price_changed")]
    [InlineData(true, true, true, 0, 1, 12, 10, "out_of_stock,price_changed")]
    public void IssuesFollowTheRules(bool visible, bool priced, bool tracked, int available, int quantity, int unit, int added, string expected) =>
        Assert.Equal(expected, string.Join(',', CartRules.Issues(visible, priced, tracked, available, quantity, unit, added)));

    [Fact]
    public void OnlyAPriceChangeDoesNotBlockCheckout()
    {
        CartLineView Line(params string[] issues) => new(1, 1, "x", 1, "s", 0, null, null, 1, 1, null, 1, null, null, null, issues);

        Assert.False(CartRules.CanCheckout([]));
        Assert.True(CartRules.CanCheckout([Line(), Line(CartIssues.PriceChanged)]));
        Assert.False(CartRules.CanCheckout([Line(), Line(CartIssues.OutOfStock)]));
        Assert.False(CartRules.CanCheckout([Line(CartIssues.Unavailable)]));
    }

    // ---- Adding ----

    [Fact]
    public async Task AddingCreatesALineWithTheServerPrice()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(price: 10);

        var result = await Add(f, id, 2);

        Assert.True(result.Succeeded);
        var line = OnlyLine(result.Value!);
        Assert.Equal((2, 10m, 20m, "Mug"), (line.Quantity, line.UnitPrice, line.LineTotal, line.Name));
        Assert.Equal((20m, 2, "USD", true), (result.Value!.Subtotal, result.Value.ItemCount, result.Value.CurrencyCode, result.Value.CanCheckout));
        Assert.Equal(10m, f.Carts.Lines.Single().AddedUnitPrice);
    }

    [Fact]
    public async Task AddingTheSameChoiceAgainAddsToTheLine()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();

        await Add(f, id, 2);
        var result = await Add(f, id, 3);

        Assert.Single(f.Carts.Lines);
        Assert.Equal(5, OnlyLine(result.Value!).Quantity);
    }

    [Fact]
    public async Task TheTotalQuantityDecidesTheTierPrice()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(price: 10, stock: 50);
        var pricing = new ProductPricingService(f.Products, f.Vendors, new FakePrimaryCurrency(), f.Audit, f.Clock);
        await pricing.SetForVendorAsync(Shop, id, new SavePricingCommand(null, null, null, [new TierPrice(5, 8)]), Seller, CancellationToken.None);

        await Add(f, id, 3);
        var result = await Add(f, id, 3);

        var line = OnlyLine(result.Value!);
        Assert.Equal((6, 8m, 48m, PriceRule.Tier), (line.Quantity, line.UnitPrice, line.LineTotal, line.AppliedRule));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10_001)]
    public async Task QuantityIsValidated(int quantity)
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 20_000);

        Assert.Contains("quantity", (await Add(f, id, quantity)).Errors.Keys);
        Assert.Empty(f.Carts.Lines);
    }

    [Fact]
    public async Task ALineCannotGrowPastTheMaximum()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 20_000);
        await Add(f, id, 9_000);

        Assert.Contains("quantity", (await Add(f, id, 2_000)).Errors.Keys);
        Assert.Equal(9_000, f.Carts.Lines.Single().Quantity);
    }

    [Fact]
    public async Task OnlyProductsACustomerCanSeeCanBeAdded()
    {
        var f = new Fixture();
        var draft = await f.LiveAsync();
        f.Product(draft).Status = ProductStatus.Draft;
        var stopped = await f.LiveAsync();
        f.Product(stopped).Status = ProductStatus.Stopped;
        var hidden = await f.LiveAsync();
        f.Product(hidden).Status = ProductStatus.HiddenByAdmin;
        var inactiveShop = await f.LiveAsync();
        f.Product(inactiveShop).VendorActive = false;
        var scheduled = await f.LiveAsync();
        f.Product(scheduled).AvailableStartUtc = Now.AddDays(1);

        foreach (var id in new[] { draft, stopped, hidden, inactiveShop, scheduled, 9999 })
            Assert.Equal(CatalogErrors.NotFound, (await Add(f, id)).ErrorCode);
        Assert.Contains("productId", (await Add(f, 0)).Errors.Keys);
        Assert.Empty(f.Carts.Lines);
    }

    [Fact]
    public async Task AMemberCannotBuyFromTheirOwnShop()
    {
        var f = new Fixture();
        var own = await f.LiveAsync(vendorId: Shop);
        var foreign = await f.LiveAsync(vendorId: OtherShop);
        f.Members.Members.Add(new VendorMember { CustomerId = Buyer, Email = "m@example.com" });

        // The fake has no shop column; it answers for every shop, so check the code and the other buyer.
        Assert.Equal(CartErrors.OwnProduct, (await Add(f, own)).ErrorCode);
        Assert.Equal(CartErrors.OwnProduct, (await Add(f, foreign)).ErrorCode);
        Assert.True((await Add(f, own, 1, OtherBuyer)).Succeeded);
    }

    [Fact]
    public async Task AtMostFiftyLinesAreAllowed()
    {
        var f = new Fixture();
        for (var i = 0; i < CartLimits.MaxLines; i++) await Add(f, await f.LiveAsync("P" + i));
        var extra = await f.LiveAsync("Extra");

        Assert.Equal(CartErrors.LineLimit, (await Add(f, extra)).ErrorCode);
        // Adding to an existing line is still fine.
        Assert.True((await Add(f, f.Carts.Lines[0].ProductId)).Succeeded);
    }

    [Fact]
    public async Task TwoRequestsAddingTheSameChoiceEndUpWithOneLine()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        f.Carts.BeforeInsert = line =>
        {
            f.Carts.BeforeInsert = null;
            f.Carts.Lines.Add(new CartLine { Id = 77, CustomerId = line.CustomerId, ProductId = line.ProductId, ValueIds = line.ValueIds, Quantity = 1, AddedUnitPrice = 10 });
        };

        var result = await Add(f, id, 2);

        Assert.True(result.Succeeded);
        Assert.Single(f.Carts.Lines);
        Assert.Equal(3, f.Carts.Lines[0].Quantity);
    }

    // ---- Stock ----

    [Fact]
    public async Task TrackedProductsCannotGoAboveTheAvailableStock()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 3);

        var result = await Add(f, id, 4);
        Assert.Equal("Only 3 available.", result.Errors["quantity"][0]);

        await Add(f, id, 2);
        // The line already holds 2, so one more is fine and two more is not.
        Assert.Contains("quantity", (await Add(f, id, 2)).Errors.Keys);
        Assert.True((await Add(f, id, 1)).Succeeded);
        Assert.Equal(3, f.Carts.Lines.Single().Quantity);
    }

    [Fact]
    public async Task AnItemOutOfStockCannotBeAdded()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 0);

        Assert.Equal("This item is out of stock.", (await Add(f, id)).Errors["quantity"][0]);
    }

    [Fact]
    public async Task ReservedStockIsNotAvailableToTheCart()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 5);
        var inventory = new InventoryService(f.Inventory, f.Products, f.Vendors, f.Attributes, f.Audit, f.Clock);
        await inventory.ReserveAsync("checkout-1", id, null, 4, null, CancellationToken.None);

        Assert.Contains("quantity", (await Add(f, id, 2)).Errors.Keys);
        Assert.True((await Add(f, id, 1)).Succeeded);
    }

    [Fact]
    public async Task ProductsWithoutStockTrackingAlwaysFit()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 0);
        f.Product(id).TrackInventory = false;

        Assert.True((await Add(f, id, 500)).Succeeded);
    }

    // ---- Variants ----

    private static SaveVariantsCommand ColorSize() => new(
        [new VariantAttributeInput(1, true, [new VariantValueInput("Red"), new VariantValueInput("Blue")]),
         new VariantAttributeInput(2, true, [new VariantValueInput("S"), new VariantValueInput("M", null, 1)])],
        [new VariantCombinationInput([0, 0], "RS", 2, null), new VariantCombinationInput([0, 1], "RM", 5, null), new VariantCombinationInput([1, 0], "BS", 5, null)]);

    private static int ValueId(ProductAttributeDetail detail, string name) => detail.Mappings.SelectMany(m => m.Values).Single(v => v.Name == name).Id;

    [Fact]
    public async Task VariantsNeedAFullChoiceAndShowTheirLabelAndSku()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(price: 10);
        var detail = await f.VariantsAsync(id, ColorSize());

        Assert.Contains("valueIds", (await Add(f, id)).Errors.Keys);
        Assert.Contains("valueIds", (await Add(f, id, 1, Buyer, ValueId(detail, "Red"))).Errors.Keys);

        var result = await Add(f, id, 1, Buyer, ValueId(detail, "M"), ValueId(detail, "Red"));
        var line = OnlyLine(result.Value!);

        // The label follows the order of the options, whatever order the values were sent in.
        Assert.Equal(("Red / M", "RM", 11m), (line.VariantLabel, line.Sku, line.UnitPrice));
        Assert.Equal(5, line.AvailableQuantity);
    }

    [Fact]
    public async Task EachVariantChoiceIsItsOwnLineAndItsOwnStock()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        var detail = await f.VariantsAsync(id, ColorSize());
        int[] redS = [ValueId(detail, "Red"), ValueId(detail, "S")];
        int[] blueS = [ValueId(detail, "Blue"), ValueId(detail, "S")];

        Assert.True((await Add(f, id, 2, Buyer, redS)).Succeeded);
        Assert.True((await Add(f, id, 1, Buyer, blueS)).Succeeded);
        Assert.Equal(2, f.Carts.Lines.Count);
        // Red-S has 2 in stock.
        Assert.Contains("quantity", (await Add(f, id, 1, Buyer, redS)).Errors.Keys);
    }

    [Fact]
    public async Task ASavedVariantChangeLeavesTheLineMarkedUnavailable()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        var detail = await f.VariantsAsync(id, ColorSize());
        await Add(f, id, 1, Buyer, ValueId(detail, "Red"), ValueId(detail, "S"));

        // The shop saves its variants again: every value gets a new id.
        await f.VariantsAsync(id, ColorSize());
        var view = await f.Create().GetAsync(Buyer, CancellationToken.None);

        var line = OnlyLine(view);
        Assert.Contains(CartIssues.VariantUnavailable, line.Issues);
        Assert.False(view.CanCheckout);
        Assert.Equal(0m, view.Subtotal);
    }

    // ---- Changing and removing ----

    [Fact]
    public async Task QuantityCanBeChangedWithTheSameChecksAndPricing()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(stock: 4);
        var lineId = OnlyLine((await Add(f, id)).Value!).Id;
        var service = f.Create();

        Assert.Contains("quantity", (await service.SetQuantityAsync(Buyer, lineId, 0, CancellationToken.None)).Errors.Keys);
        Assert.Contains("quantity", (await service.SetQuantityAsync(Buyer, lineId, 10_001, CancellationToken.None)).Errors.Keys);
        Assert.Equal("Only 4 available.", (await service.SetQuantityAsync(Buyer, lineId, 5, CancellationToken.None)).Errors["quantity"][0]);

        var result = await service.SetQuantityAsync(Buyer, lineId, 4, CancellationToken.None);
        Assert.Equal((4, 40m), (OnlyLine(result.Value!).Quantity, OnlyLine(result.Value!).LineTotal));
    }

    [Fact]
    public async Task AnotherCustomersLineIsNotFound()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        var lineId = OnlyLine((await Add(f, id)).Value!).Id;
        var service = f.Create();

        Assert.Equal(CatalogErrors.NotFound, (await service.SetQuantityAsync(OtherBuyer, lineId, 2, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.RemoveAsync(OtherBuyer, lineId, CancellationToken.None)).ErrorCode);
        Assert.Empty((await service.GetAsync(OtherBuyer, CancellationToken.None)).Groups);
        Assert.Single(f.Carts.Lines);
    }

    [Fact]
    public async Task ALineOfAProductThatIsGoneCannotBeChangedButCanBeRemoved()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        var lineId = OnlyLine((await Add(f, id)).Value!).Id;
        f.Product(id).Status = ProductStatus.Stopped;
        var service = f.Create();

        Assert.Contains("quantity", (await service.SetQuantityAsync(Buyer, lineId, 2, CancellationToken.None)).Errors.Keys);
        Assert.True((await service.RemoveAsync(Buyer, lineId, CancellationToken.None)).Succeeded);
        Assert.Empty(f.Carts.Lines);
    }

    [Fact]
    public async Task ClearEmptiesOnlyTheCallersCart()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        await Add(f, id);
        await Add(f, id, 1, OtherBuyer);

        var view = await f.Create().ClearAsync(Buyer, CancellationToken.None);

        Assert.Empty(view.Groups);
        Assert.False(view.CanCheckout);
        Assert.Single(f.Carts.Lines);
        Assert.Equal(1, await f.Create().CountAsync(OtherBuyer, CancellationToken.None));
    }

    // ---- The view ----

    [Fact]
    public async Task LinesAreGroupedByShopWithTheirOwnSubtotals()
    {
        var f = new Fixture();
        var a = await f.LiveAsync("A", 10, vendorId: Shop);
        var b = await f.LiveAsync("B", 5, vendorId: OtherShop);
        var c = await f.LiveAsync("C", 2, vendorId: Shop);
        await Add(f, a, 2);
        await Add(f, b, 1);
        await Add(f, c, 3);

        var view = await f.Create().GetAsync(Buyer, CancellationToken.None);

        Assert.Equal([Shop, OtherShop], view.Groups.Select(g => g.VendorId));
        Assert.Equal([26m, 5m], view.Groups.Select(g => g.Subtotal));
        Assert.Equal((31m, 6), (view.Subtotal, view.ItemCount));
        Assert.Equal(["A", "C"], view.Groups[0].Lines.Select(l => l.Name));
    }

    [Fact]
    public async Task EveryIssueAppearsWhenTheProductChangesAfterAdding()
    {
        var f = new Fixture();
        var gone = await f.LiveAsync("Gone", 10, 10);
        var empty = await f.LiveAsync("Empty", 10, 10);
        var low = await f.LiveAsync("Low", 10, 10);
        var fine = await f.LiveAsync("Fine", 10, 10);
        foreach (var id in new[] { gone, empty, low, fine }) await Add(f, id, 3);

        f.Product(gone).Status = ProductStatus.Stopped;
        f.Product(empty).StockQuantity = 0;
        f.Product(low).StockQuantity = 2;
        var view = await f.Create().GetAsync(Buyer, CancellationToken.None);

        string Issues(string name) => string.Join(',', view.Groups.SelectMany(g => g.Lines).Single(l => l.Name == name).Issues);
        Assert.Equal(CartIssues.Unavailable, Issues("Gone"));
        Assert.Equal(CartIssues.OutOfStock, Issues("Empty"));
        Assert.Equal(CartIssues.InsufficientStock, Issues("Low"));
        Assert.Equal(string.Empty, Issues("Fine"));
        Assert.False(view.CanCheckout);
        // The stopped product is not counted; the others still are.
        Assert.Equal(90m, view.Subtotal);
    }

    [Fact]
    public async Task APriceChangeIsReportedUntilTheCustomerAcceptsIt()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(price: 10);
        await Add(f, id, 2);
        f.Product(id).Price = 12;
        var service = f.Create();

        var changed = OnlyLine(await service.GetAsync(Buyer, CancellationToken.None));
        Assert.Equal((12m, 10m, 24m), (changed.UnitPrice, changed.PreviousUnitPrice, changed.LineTotal));
        Assert.Contains(CartIssues.PriceChanged, changed.Issues);
        // A price change warns but does not block checkout.
        Assert.True((await service.GetAsync(Buyer, CancellationToken.None)).CanCheckout);

        var accepted = OnlyLine(await service.AcceptPricesAsync(Buyer, CancellationToken.None));
        Assert.Null(accepted.PreviousUnitPrice);
        Assert.DoesNotContain(CartIssues.PriceChanged, accepted.Issues);
        Assert.Equal(12m, f.Carts.Lines.Single().AddedUnitPrice);
    }

    [Fact]
    public async Task ChangingTheQuantityRefreshesTheSavedPrice()
    {
        var f = new Fixture();
        var id = await f.LiveAsync(price: 10);
        var lineId = OnlyLine((await Add(f, id)).Value!).Id;
        f.Product(id).Price = 12;

        var result = await f.Create().SetQuantityAsync(Buyer, lineId, 2, CancellationToken.None);

        Assert.Null(OnlyLine(result.Value!).PreviousUnitPrice);
        Assert.Equal(12m, f.Carts.Lines.Single().AddedUnitPrice);
    }

    [Fact]
    public async Task ADeletedProductSilentlyLeavesTheCart()
    {
        var f = new Fixture();
        var id = await f.LiveAsync();
        await Add(f, id);
        f.Product(id).Deleted = true;

        Assert.Empty((await f.Create().GetAsync(Buyer, CancellationToken.None)).Groups);
    }

    [Fact]
    public async Task TheCountIsTheNumberOfUnits()
    {
        var f = new Fixture();
        await Add(f, await f.LiveAsync("A"), 2);
        await Add(f, await f.LiveAsync("B"), 3);

        Assert.Equal(5, await f.Create().CountAsync(Buyer, CancellationToken.None));
        Assert.Equal(0, await f.Create().CountAsync(OtherBuyer, CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyCartCannotCheckOut()
    {
        var view = await new Fixture().Create().GetAsync(Buyer, CancellationToken.None);

        Assert.Equal(("USD", 0m, 0, false), (view.CurrencyCode, view.Subtotal, view.ItemCount, view.CanCheckout));
    }
}
