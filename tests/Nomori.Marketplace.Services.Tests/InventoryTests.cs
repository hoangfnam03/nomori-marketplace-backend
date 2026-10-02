using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Catalog;
using static Nomori.Marketplace.Services.Tests.ProductOwnershipTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class InventoryTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Seller = 10;

    // ---- Pure rules ----

    [Theory]
    [InlineData(10, 0, 10)]
    [InlineData(10, 4, 6)]
    [InlineData(3, 5, 0)]
    public void AvailableIsNeverNegative(int onHand, int reserved, int expected) =>
        Assert.Equal(expected, StockRules.Available(onHand, reserved));

    [Theory]
    [InlineData(5, 0, -5, true)]
    [InlineData(5, 0, -6, false)]
    [InlineData(5, 3, -2, true)]
    [InlineData(5, 3, -3, false)]
    [InlineData(5, 3, 10, true)]
    public void AdjustmentsCannotGoBelowZeroOrBelowReserved(int onHand, int reserved, int delta, bool allowed) =>
        Assert.Equal(allowed, StockRules.CanAdjust(onHand, reserved, delta));

    [Theory]
    [InlineData(5, 0, 5, true)]
    [InlineData(5, 0, 6, false)]
    [InlineData(5, 4, 1, true)]
    [InlineData(5, 4, 2, false)]
    [InlineData(5, 0, 0, false)]
    public void ReservationsNeedEnoughAvailableStock(int onHand, int reservedByOthers, int quantity, bool allowed) =>
        Assert.Equal(allowed, StockRules.CanReserve(onHand, reservedByOthers, quantity));

    [Fact]
    public void LowStockNeedsTrackingAndAtOrBelowTheThreshold()
    {
        Assert.True(StockRules.IsLow(true, 5, 5));
        Assert.False(StockRules.IsLow(true, 6, 5));
        Assert.False(StockRules.IsLow(false, 0, 5));
        Assert.True(new Product { StockQuantity = 2 }.IsLowStock);
    }

    // ---- Service ----

    private sealed class Fixture
    {
        public FakeVendorStore Vendors { get; } = new();
        public FakeProductStore Products { get; } = new();
        public FakeAttributeStore Attributes { get; } = new();
        public FakeInventoryStore Inventory { get; }
        public RecordingAuditLog Audit { get; } = new();
        public TestClock Clock { get; } = new();

        public Fixture()
        {
            Inventory = new FakeInventoryStore(Products, Attributes);
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Shop", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Other", Active = true });
        }

        public InventoryService Create() => new(Inventory, Products, Vendors, Attributes, Audit, Clock);

        public ProductService CreateProducts() => new(
            Products, Inventory, new UnusedCategoryStore(), new UnusedManufacturerStore(), Vendors, new NoMembers(), new FakeMediaStore(), new FakeTaxonomy(), Audit,
            new RecordingEmailSender(), TestOptions.Email(false), NullLog<ProductService>.Instance, Clock);

        public async Task<int> ProductAsync(int stock = 10, int vendorId = Shop)
        {
            var result = await CreateProducts().CreateForVendorAsync(vendorId,
                new SaveVendorProductCommand("Mug", null, null, 10, 0, stock, [1], []), Seller, CancellationToken.None);
            return result.Value!.Id;
        }

        /// <summary>A product with two combinations holding the given stocks; the product total follows.</summary>
        public int ProductWithCombinations(int first, int second)
        {
            var id = ProductAsync(0).GetAwaiter().GetResult();
            Attributes.Combinations.Add(new ProductAttributeCombination { Id = 501, ProductId = id, StockQuantity = first, Sku = "A" });
            Attributes.Combinations.Add(new ProductAttributeCombination { Id = 502, ProductId = id, StockQuantity = second, Sku = "B" });
            Products.Products.Single(p => p.Id == id).StockQuantity = first + second;
            return id;
        }
    }

    private static Task<CatalogResult<InventoryOverview>> Adjust(InventoryService s, int productId, int delta, string? reason = "restock", int? combinationId = null, string? note = null, int vendorId = Shop) =>
        s.AdjustForVendorAsync(vendorId, productId, new AdjustStockCommand(combinationId, delta, reason, note), Seller, CancellationToken.None);

    [Fact]
    public async Task AdjustChangesStockWritesTheLedgerAndAudits()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(10);

        var result = await Adjust(service, id, 5, "Restock", note: " new delivery ");

        Assert.True(result.Succeeded);
        Assert.Equal(15, result.Value!.Product.OnHand);
        var movement = f.Inventory.Movements.Last();
        Assert.Equal((5, 15, "restock", "new delivery"), (movement.Delta, movement.QuantityAfter, movement.Reason, movement.Note));
        Assert.Equal(Seller, movement.ActorCustomerId);
        Assert.Contains(f.Audit.Entries, e => e.Event == "product.stock_adjusted");
    }

    [Fact]
    public async Task AdjustValidatesDeltaReasonAndNote()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync();

        Assert.Contains("delta", (await Adjust(service, id, 0)).Errors.Keys);
        Assert.Contains("delta", (await Adjust(service, id, InventoryLimits.MaxDelta + 1)).Errors.Keys);
        Assert.Contains("reason", (await Adjust(service, id, 1, "sale")).Errors.Keys);
        Assert.Contains("reason", (await Adjust(service, id, 1, null)).Errors.Keys);
        Assert.Contains("note", (await Adjust(service, id, 1, "restock", note: new string('n', 501))).Errors.Keys);
        Assert.True((await Adjust(service, id, 1, "damage")).Succeeded);
        Assert.True((await Adjust(service, id, 1, "return")).Succeeded);
        Assert.True((await Adjust(service, id, 1, "correction")).Succeeded);
    }

    [Fact]
    public async Task StockCannotGoNegativeOrBelowWhatIsReserved()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(5);

        var tooMuch = await Adjust(service, id, -6);
        Assert.Equal(CatalogErrors.InsufficientStock, tooMuch.ErrorCode);
        Assert.Equal(5, f.Products.Products.Single(p => p.Id == id).StockQuantity);

        Assert.True((await service.ReserveAsync("cart-1", id, null, 3, null, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.InsufficientStock, (await Adjust(service, id, -3)).ErrorCode);
        Assert.True((await Adjust(service, id, -2)).Succeeded);
    }

    [Fact]
    public async Task ProductsWithVariantsAdjustPerCombinationAndTheTotalFollows()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = f.ProductWithCombinations(3, 4);

        Assert.Contains("combinationId", (await Adjust(service, id, 1)).Errors.Keys);
        Assert.Contains("combinationId", (await Adjust(service, id, 1, combinationId: 999)).Errors.Keys);

        var result = await Adjust(service, id, 6, combinationId: 502);
        Assert.True(result.Succeeded);
        Assert.Equal(13, result.Value!.Product.OnHand);
        Assert.Equal([3, 10], result.Value.Combinations.Select(c => c.Level.OnHand));
        Assert.Equal(502, f.Inventory.Movements.Last().CombinationId);
    }

    [Fact]
    public async Task APlainProductRefusesACombinationId()
    {
        var f = new Fixture();
        var id = await f.ProductAsync();

        Assert.Contains("combinationId", (await Adjust(f.Create(), id, 1, combinationId: 501)).Errors.Keys);
    }

    [Fact]
    public async Task IsolationAndInactiveShops()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync();

        Assert.Equal(CatalogErrors.NotFound, (await Adjust(service, id, 1, vendorId: OtherShop)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.GetOverviewForVendorAsync(OtherShop, id, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.GetMovementsForVendorAsync(OtherShop, id, 1, 20, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.SetSettingsForVendorAsync(OtherShop, id, true, 5, Seller, CancellationToken.None)).ErrorCode);

        f.Vendors.Vendors.Single(v => v.Id == Shop).Active = false;
        Assert.Equal(CatalogErrors.Forbidden, (await Adjust(service, id, 1)).ErrorCode);
        Assert.Equal(CatalogErrors.Forbidden, (await service.SetSettingsForVendorAsync(Shop, id, true, 5, Seller, CancellationToken.None)).ErrorCode);
        Assert.True((await service.GetOverviewForVendorAsync(Shop, id, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task StockCanBeAdjustedWhileHidden()
    {
        var f = new Fixture();
        var id = await f.ProductAsync();
        await f.CreateProducts().HideAsync(id, "Reason", 1, CancellationToken.None);

        Assert.True((await Adjust(f.Create(), id, 1)).Succeeded);
    }

    [Fact]
    public async Task SettingsAreValidatedAndDriveTheLowStockFlag()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(8);

        Assert.Contains("lowStockThreshold", (await service.SetSettingsForVendorAsync(Shop, id, true, -1, Seller, CancellationToken.None)).Errors.Keys);
        Assert.Contains("lowStockThreshold", (await service.SetSettingsForVendorAsync(Shop, id, true, InventoryLimits.MaxThreshold + 1, Seller, CancellationToken.None)).Errors.Keys);
        Assert.False(f.Products.Products.Single(p => p.Id == id).IsLowStock);

        var result = await service.SetSettingsForVendorAsync(Shop, id, true, 10, Seller, CancellationToken.None);
        Assert.True(result.Succeeded);
        Assert.True(f.Products.Products.Single(p => p.Id == id).IsLowStock);
        Assert.Equal(10, result.Value!.LowStockThreshold);

        await service.SetSettingsForVendorAsync(Shop, id, false, 10, Seller, CancellationToken.None);
        Assert.False(f.Products.Products.Single(p => p.Id == id).IsLowStock);
    }

    // ---- Reservations ----

    [Fact]
    public async Task ReservingHoldsStockWithoutChangingOnHand()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(5);

        Assert.True((await service.ReserveAsync("cart-1", id, null, 2, null, CancellationToken.None)).Succeeded);

        var availability = (await service.GetAvailabilityAsync(id, CancellationToken.None))!;
        Assert.Equal(3, availability.Product);
        Assert.Equal(5, f.Products.Products.Single(p => p.Id == id).StockQuantity);
        Assert.Equal(f.Clock.UtcNow.AddMinutes(InventoryLimits.DefaultReservationMinutes), f.Inventory.Holds.Single().ExpiresOnUtc);
    }

    [Fact]
    public async Task TheLastUnitGoesToTheFirstReference()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(1);

        Assert.True((await service.ReserveAsync("a", id, null, 1, null, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.InsufficientStock, (await service.ReserveAsync("b", id, null, 1, null, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ReservingAgainWithTheSameReferenceReplacesTheHold()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(5);

        await service.ReserveAsync("a", id, null, 2, null, CancellationToken.None);
        Assert.True((await service.ReserveAsync("a", id, null, 5, 60, CancellationToken.None)).Succeeded);

        Assert.Single(f.Inventory.Holds);
        Assert.Equal(5, f.Inventory.Holds[0].Quantity);
        Assert.Equal(0, (await service.GetAvailabilityAsync(id, CancellationToken.None))!.Product);
    }

    [Fact]
    public async Task ExpiredHoldsStopCounting()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(1);
        await service.ReserveAsync("a", id, null, 1, 1, CancellationToken.None);
        f.Inventory.Holds[0].ExpiresOnUtc = f.Clock.UtcNow.AddSeconds(-1);

        Assert.True((await service.ReserveAsync("b", id, null, 1, null, CancellationToken.None)).Succeeded);
        Assert.Equal(CatalogErrors.ReservationExpired, (await service.CommitAsync("a", CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ReleaseFreesTheStock()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(2);
        await service.ReserveAsync("a", id, null, 2, null, CancellationToken.None);

        Assert.Equal(1, await service.ReleaseAsync("a", CancellationToken.None));
        Assert.Equal(0, await service.ReleaseAsync("a", CancellationToken.None));
        Assert.Equal(2, (await service.GetAvailabilityAsync(id, CancellationToken.None))!.Product);
    }

    [Fact]
    public async Task CommitTurnsTheHoldIntoASale()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = f.ProductWithCombinations(5, 5);
        await service.ReserveAsync("order-1", id, 501, 2, null, CancellationToken.None);
        await service.ReserveAsync("order-1", id, 502, 1, null, CancellationToken.None);

        Assert.True((await service.CommitAsync("order-1", CancellationToken.None)).Succeeded);

        Assert.Equal([3, 4], f.Attributes.Combinations.Select(c => c.StockQuantity));
        Assert.Equal(7, f.Products.Products.Single(p => p.Id == id).StockQuantity);
        Assert.Equal(2, f.Inventory.Movements.Count(m => m.Reason == StockReasons.Sale && m.Reference == "order-1"));
        // Committed holds do not count as reserved any more.
        Assert.Equal(3, (await service.GetAvailabilityAsync(id, CancellationToken.None))!.Combinations[501]);
        // A second commit finds nothing active.
        Assert.Equal(CatalogErrors.ReservationExpired, (await service.CommitAsync("order-1", CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ReservationInputIsValidated()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(5);
        var combos = f.ProductWithCombinations(1, 1);

        Task<CatalogResult<bool>> Try(string reference, int quantity, int? minutes = null, int product = -1, int? combination = null) =>
            service.ReserveAsync(reference, product == -1 ? id : product, combination, quantity, minutes, CancellationToken.None);

        Assert.Contains("reference", (await Try(" ", 1)).Errors.Keys);
        Assert.Contains("reference", (await Try(new string('r', 101), 1)).Errors.Keys);
        Assert.Contains("quantity", (await Try("a", 0)).Errors.Keys);
        Assert.Contains("quantity", (await Try("a", 1001)).Errors.Keys);
        Assert.Contains("minutes", (await Try("a", 1, 0)).Errors.Keys);
        Assert.Contains("minutes", (await Try("a", 1, 1441)).Errors.Keys);
        Assert.Equal(CatalogErrors.NotFound, (await Try("a", 1, null, 9999)).ErrorCode);
        // Variants need a combination, plain products must not name one.
        Assert.Contains("combinationId", (await Try("a", 1, null, combos)).Errors.Keys);
        Assert.Contains("combinationId", (await Try("a", 1, null, -1, 501)).Errors.Keys);
    }

    [Fact]
    public async Task UntrackedProductsAreAlwaysAvailableAndHoldNothing()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(0);
        await service.SetSettingsForVendorAsync(Shop, id, false, 5, Seller, CancellationToken.None);

        Assert.True((await service.ReserveAsync("a", id, null, 5, null, CancellationToken.None)).Succeeded);

        Assert.Empty(f.Inventory.Holds);
        Assert.False((await service.GetAvailabilityAsync(id, CancellationToken.None))!.TrackInventory);
    }

    [Fact]
    public async Task MovementsAreListedNewestFirstWithPaging()
    {
        var f = new Fixture();
        var service = f.Create();
        var id = await f.ProductAsync(10);
        await Adjust(service, id, 1);
        await Adjust(service, id, 2);

        var page = (await service.GetMovementsForVendorAsync(Shop, id, 1, 2, CancellationToken.None)).Value!;

        Assert.Equal(3, page.TotalCount);
        Assert.Equal([2, 1], page.Items.Select(m => m.Delta));
        Assert.Equal(2, page.TotalPages);
    }

    // ---- Product service integration ----

    [Fact]
    public async Task CreatingAProductWritesTheInitialLedgerRow()
    {
        var f = new Fixture();
        var id = await f.ProductAsync(7);
        var none = await f.ProductAsync(0);

        var movement = f.Inventory.Movements.Single(m => m.ProductId == id);
        Assert.Equal((7, 7, StockReasons.Initial), (movement.Delta, movement.QuantityAfter, movement.Reason));
        Assert.DoesNotContain(f.Inventory.Movements, m => m.ProductId == none);
    }

    [Fact]
    public async Task SellerSaveNeverChangesStock()
    {
        var f = new Fixture();
        var products = f.CreateProducts();
        var id = await f.ProductAsync(7);

        var saved = await products.UpdateForVendorAsync(Shop, id, new SaveVendorProductCommand("Mug", null, null, 10, 0, 999, [1], []), Seller, CancellationToken.None);

        Assert.Equal(7, saved.Value!.StockQuantity);
        Assert.Single(f.Inventory.Movements);
    }

    [Fact]
    public async Task AdminEditGoesThroughTheLedgerAndRespectsReservations()
    {
        var f = new Fixture();
        var products = f.CreateProducts();
        var id = await f.ProductAsync(7);
        UpdateProductCommand Edit(int stock) => new(id, "Mug", null, null, 10, 0, stock, true, null, false, 0, [1], []);

        var up = await products.UpdateAsync(Edit(10), 1, CancellationToken.None);
        Assert.Equal(10, up.Value!.StockQuantity);
        Assert.Equal((3, 10, StockReasons.AdminEdit), (f.Inventory.Movements.Last().Delta, f.Inventory.Movements.Last().QuantityAfter, f.Inventory.Movements.Last().Reason));

        await f.Create().ReserveAsync("cart", id, null, 8, null, CancellationToken.None);
        var refused = await products.UpdateAsync(Edit(5), 1, CancellationToken.None);
        Assert.Contains("stockQuantity", refused.Errors.Keys);
        Assert.Equal(10, f.Products.Products.Single(p => p.Id == id).StockQuantity);
    }

    [Fact]
    public async Task AdminEditIgnoresStockWhileTheProductHasVariants()
    {
        var f = new Fixture();
        var products = f.CreateProducts();
        var id = await f.ProductAsync(7);
        f.Products.ProductsWithVariants.Add(id);

        var saved = await products.UpdateAsync(new UpdateProductCommand(id, "Mug", null, null, 10, 0, 999, true, null, false, 0, [1], []), 1, CancellationToken.None);

        Assert.Equal(7, saved.Value!.StockQuantity);
    }

    [Fact]
    public async Task VariantsCannotBeSavedWhileStockIsReserved()
    {
        var f = new Fixture();
        var id = f.ProductWithCombinations(3, 3);
        await f.Create().ReserveAsync("cart", id, 501, 1, null, CancellationToken.None);
        f.Attributes.Attributes.Add(new ProductAttributeSpec { Id = 1, Name = "Color" });
        var details = new VendorProductDetailsService(f.Products, f.Vendors, f.Attributes, new ProductAttributeService(f.Attributes),
            new FakeSpecificationStore(), f.Inventory, f.Audit, f.Clock);
        var command = new SaveVariantsCommand([new VariantAttributeInput(1, true, [new VariantValueInput("Red")])], [new VariantCombinationInput([0], null, 4, null)]);

        var refused = await details.SetVariantsAsync(Shop, id, command, Seller, CancellationToken.None);
        Assert.Equal(CatalogErrors.ActiveReservations, refused.ErrorCode);

        await f.Create().ReleaseAsync("cart", CancellationToken.None);
        Assert.True((await details.SetVariantsAsync(Shop, id, command, Seller, CancellationToken.None)).Succeeded);
        Assert.Equal(Seller, f.Attributes.LastActor);
    }
}
