using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Checkout;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Checkout;
using Nomori.Marketplace.Services.Discounts;
using Nomori.Marketplace.Services.Orders;
using Nomori.Marketplace.Services.Payments;
using static Nomori.Marketplace.Services.Tests.DiscountTests;
using static Nomori.Marketplace.Services.Tests.OrderTests;
using static Nomori.Marketplace.Services.Tests.PaymentTests;
using static Nomori.Marketplace.Services.Tests.ShippingTests;

namespace Nomori.Marketplace.Services.Tests;

public sealed class CheckoutTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Buyer = 100;
    private const int OtherBuyer = 101;
    private const int Address = 7;
    private const string Key = "key-12345678";

    private static readonly string[] ChoiceFields = ["addressId", "shippingChoices", "paymentMethod"];

    private sealed class FakeCart : ICartService
    {
        public CartView View { get; set; } = new("USD", [], 0, 0, false);
        public int Cleared { get; private set; }

        public Task<CartView> GetAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(View);

        public Task<CartView> ClearAsync(int customerId, CancellationToken cancellationToken)
        {
            Cleared++;
            View = new CartView("USD", [], 0, 0, false);
            return Task.FromResult(View);
        }

        public Task<int> CountAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> AddAsync(int customerId, AddToCartCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> SetQuantityAsync(int customerId, int lineId, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> RemoveAsync(int customerId, int lineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> AcceptPricesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCartLines : ICartStore
    {
        public List<CartLine> Lines { get; } = [];

        public Task<IReadOnlyList<CartLine>> GetLinesAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartLine>>(Lines.Where(l => l.CustomerId == customerId).ToList());

        public Task<CartLine?> GetLineAsync(int customerId, int lineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartLine?> FindAsync(int customerId, int productId, string valueIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> InsertAsync(CartLine line, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateLineAsync(int lineId, int quantity, decimal addedUnitPrice, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetAddedUnitPriceAsync(int lineId, decimal addedUnitPrice, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(int customerId, int lineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ClearAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> CountUnitsAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePrices : IPriceCalculationService
    {
        public Dictionary<int, decimal> Unit { get; } = new() { [1] = 10m, [2] = 20m, [3] = 5m };

        public Task<CatalogResult<PriceQuote>> QuoteAsync(PriceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(CatalogResult.Success(new PriceQuote(
                request.ProductId, null, request.Quantity, "USD", Unit[request.ProductId], Unit[request.ProductId], null,
                Unit[request.ProductId] * request.Quantity, PriceRule.Base)));
    }

    /// <summary>Shipping options per shop for the address; a closed destination refuses the quote like the real service.</summary>
    private sealed class FakeShipping(FakeCart cart) : IShippingService
    {
        public Dictionary<int, List<ShippingOption>> Options { get; } = [];
        public bool DestinationClosed { get; set; }

        public Task<CatalogResult<ShippingQuote>> QuoteAsync(int customerId, ShippingQuoteRequest request, CancellationToken cancellationToken)
        {
            if (DestinationClosed) return Task.FromResult(CatalogResult.Failure<ShippingQuote>("addressId", "We cannot ship to this address."));
            var shops = cart.View.Groups.Select(g =>
            {
                var options = Options.GetValueOrDefault(g.VendorId) ?? [];
                return new ShippingShopQuote(g.VendorId, g.VendorName, g.Subtotal, options, options.Count > 0);
            }).ToList();
            return Task.FromResult(CatalogResult.Success(new ShippingQuote("USD", "US", null, shops, shops.All(s => s.CanShip), null)));
        }

        public Task<IReadOnlyList<ShippingRate>> GetRatesAsync(int vendorId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShippingRate>> CreateRateAsync(int vendorId, SaveShippingRateCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<ShippingRate>> UpdateRateAsync(int vendorId, int id, SaveShippingRateCommand command, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<bool>> DeleteRateAsync(int vendorId, int id, int actorCustomerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public FakeCart Cart { get; } = new();
        public FakeCartLines CartLines { get; } = new();
        public FakePrices Prices { get; } = new();
        public FakeShipping Shipping { get; }
        public FakePaymentStore PaymentStore { get; } = new();
        public FlakyGateway Flaky { get; } = new();
        public FakeOrderStore OrderStore { get; } = new();
        public FakeVendorStore Vendors { get; } = new();
        public FakeStockService Stock { get; } = new();
        public StubAddresses Addresses { get; } = new();
        public FakeDiscountStore Discounts { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public Fixture()
        {
            Shipping = new FakeShipping(Cart);
            Vendors.Vendors.Add(new Vendor { Id = Shop, Name = "Mugs Inc", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = OtherShop, Name = "Tea Co", Active = true });
            PaymentStore.Methods.Add(new PaymentMethodSetting { SystemName = "flaky", Enabled = true });
            PaymentStore.Methods.Single(m => m.SystemName == "sandbox").Enabled = false;
            Stock.OnHand[1] = 10;
            Stock.OnHand[2] = 10;
            Stock.OnHand[3] = 10;
            Addresses.Addresses.Add(new CustomerAddress
            {
                Id = Address, CustomerId = Buyer, FirstName = "Ann", LastName = "Lee", Address1 = "1 Main St", City = "Springfield",
                StateProvince = "IL", ZipPostalCode = "62701", CountryCode = "US", PhoneNumber = "+1 555 0100"
            });
            Shipping.Options[Shop] = [new ShippingOption(1, "Standard", 5m, false, 3, 5), new ShippingOption(2, "Express", 12m, false, 1, 2)];
            Shipping.Options[OtherShop] = [new ShippingOption(3, "Post", 3m, false, null, null)];

            // Shop: 2 x product 1 at 10. Other shop: 1 x product 2 at 20.
            SetCart((Shop, [(1, 1, 2)]), (OtherShop, [(2, 2, 1)]));
        }

        public CheckoutService Create()
        {
            var orders = new OrderService(OrderStore, Vendors, new FakePrimaryCurrency(), Stock, Audit, new TestClock());
            var payments = new PaymentService(PaymentStore, [new CashOnDeliveryProvider(), Flaky], new FakePrimaryCurrency(), Audit, new TestClock());
            var discounts = new DiscountService(Discounts, new FakePrimaryCurrency(), Audit, new TestClock());
            return new CheckoutService(Cart, CartLines, Prices, Shipping, payments, PaymentStore, orders, Stock, Addresses, discounts, Audit);
        }

        /// <summary>A cart of (shop, [(line id, product id, quantity)]); lines are priced from <see cref="Prices"/>.</summary>
        public void SetCart(params (int Vendor, (int LineId, int ProductId, int Quantity)[] Lines)[] shops)
        {
            CartLines.Lines.Clear();
            var groups = new List<CartShopGroup>();
            foreach (var (vendor, lines) in shops)
            {
                var views = lines.Select(l =>
                {
                    CartLines.Lines.Add(new CartLine { Id = l.LineId, CustomerId = Buyer, ProductId = l.ProductId, Quantity = l.Quantity });
                    var unit = Prices.Unit[l.ProductId];
                    return LineView(l.LineId, l.ProductId, vendor, l.Quantity, unit);
                }).ToList();
                groups.Add(new CartShopGroup(vendor, vendor == Shop ? "Mugs Inc" : "Tea Co", views, views.Sum(v => v.LineTotal)));
            }
            Cart.View = new CartView("USD", groups, groups.Sum(g => g.Subtotal), groups.Sum(g => g.Lines.Sum(l => l.Quantity)), true);
        }

        /// <summary>A code that is valid for the default cart; a shop code needs that shop in the cart.</summary>
        public Discount Code(string code = "WELCOME10", string type = "percentage", decimal value = 10, int? vendorId = null, int? maxUses = null, int? perCustomer = null)
        {
            var discount = new Discount
            {
                Id = Discounts.Discounts.Count + 1, VendorId = vendorId, Name = code, Code = code, Type = type == "fixed" ? DiscountType.Fixed : DiscountType.Percentage,
                Value = value, MaxUses = maxUses, MaxUsesPerCustomer = perCustomer, Enabled = true
            };
            Discounts.Discounts.Add(discount);
            return discount;
        }

        public void AddIssue(string issue)
        {
            var line = Cart.View.Groups[0].Lines[0] with { Issues = [issue] };
            var group = Cart.View.Groups[0] with { Lines = [line, .. Cart.View.Groups[0].Lines.Skip(1)] };
            Cart.View = Cart.View with { Groups = [group, .. Cart.View.Groups.Skip(1)] };
        }
    }

    private static CartLineView LineView(int id, int productId, int vendor, int quantity, decimal unit) =>
        new(id, productId, "Product " + productId, vendor, vendor == Shop ? "Mugs Inc" : "Tea Co", 7, "Red / S", "SKU-" + productId, quantity, unit, null, unit * quantity,
            PriceRule.Base, null, null, []);

    private static CheckoutChoices Choices(int? address = Address, string? method = "cod", params ShippingChoice[] shipping) =>
        new(address, shipping.Length == 0 ? [new ShippingChoice(Shop, 1), new ShippingChoice(OtherShop, 3)] : shipping, method);

    private static CheckoutChoices WithCode(string? code) => Choices() with { CouponCode = code };

    private static PlaceOrderRequest Request(
        int? address = Address, string? method = "cod", string? key = Key, bool terms = true, string? note = null, ShippingChoice[]? shipping = null,
        string? code = null) =>
        new(address, shipping ?? [new ShippingChoice(Shop, 1), new ShippingChoice(OtherShop, 3)], method, key, terms, note, code);

    private static Task<CatalogResult<PlacedOrder>> Place(Fixture f, PlaceOrderRequest? request = null, int customer = Buyer) =>
        f.Create().PlaceAsync(customer, request ?? Request(), CancellationToken.None);

    // ---- Pure rules ----

    [Theory]
    [InlineData("abcdefgh", true)]
    [InlineData("3f2b8a6e-1c4d-4e5f-9a0b-123456789abc", true)]
    [InlineData("abc_DEF-123", true)]
    [InlineData("short", false)]
    [InlineData("has space 123", false)]
    [InlineData("semi;colon;12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void KeysAreLettersDigitsDashAndUnderscore(string? key, bool valid) => Assert.Equal(valid, CheckoutRules.IsValidKey(key));

    [Fact]
    public void KeysAreLimitedInLength()
    {
        Assert.True(CheckoutRules.IsValidKey(new string('a', CheckoutLimits.MaxKeyLength)));
        Assert.False(CheckoutRules.IsValidKey(new string('a', CheckoutLimits.MaxKeyLength + 1)));
    }

    [Fact]
    public void KeysOfDifferentCustomersNeverMeet()
    {
        Assert.NotEqual(CheckoutRules.PlacementKey(1, "abcdefgh"), CheckoutRules.PlacementKey(2, "abcdefgh"));
        Assert.Equal("100:abcdefgh", CheckoutRules.PlacementKey(100, "abcdefgh"));
        Assert.Equal("order-9", CheckoutRules.PaymentKey(9));
        Assert.True(CheckoutRules.StockReference(CheckoutRules.PlacementKey(int.MaxValue, new string('k', 64))).Length <= InventoryLimits.MaxReferenceLength);
    }

    [Fact]
    public void CartProblemsFollowTheCart()
    {
        var empty = new CartView("USD", [], 0, 0, false);
        Assert.Equal([CheckoutProblems.CartEmpty], CheckoutRules.CartProblems(empty));

        var line = LineView(1, 1, Shop, 1, 10m);
        CartView Of(params string[] issues) =>
            new("USD", [new CartShopGroup(Shop, "s", [line with { Issues = issues }], 10)], 10, 1, true);

        Assert.Empty(CheckoutRules.CartProblems(Of()));
        Assert.Equal([CheckoutProblems.CartIssues], CheckoutRules.CartProblems(Of(CartIssues.OutOfStock)));
        Assert.Equal([CheckoutProblems.PricesChanged], CheckoutRules.CartProblems(Of(CartIssues.PriceChanged)));
        Assert.Equal([CheckoutProblems.CartIssues, CheckoutProblems.PricesChanged], CheckoutRules.CartProblems(Of(CartIssues.Unavailable, CartIssues.PriceChanged)));
    }

    [Fact]
    public void EveryProblemMapsToAFieldAndAMessage()
    {
        foreach (var problem in new[]
        {
            CheckoutProblems.AddressRequired, CheckoutProblems.AddressInvalid, CheckoutProblems.ShippingUnavailable, CheckoutProblems.ShippingNotChosen,
            CheckoutProblems.ShippingInvalid, CheckoutProblems.PaymentRequired, CheckoutProblems.PaymentInvalid
        })
        {
            Assert.False(CheckoutRules.IsCartProblem(problem));
            Assert.Contains(CheckoutRules.FieldOf(problem), ChoiceFields);
            Assert.NotEmpty(CheckoutRules.MessageOf(problem));
        }
        Assert.All(new[] { CheckoutProblems.CartEmpty, CheckoutProblems.CartIssues, CheckoutProblems.PricesChanged }, p => Assert.True(CheckoutRules.IsCartProblem(p)));
    }

    // ---- Preview ----

    [Fact]
    public async Task AValidPreviewHasTotalsAndCanBePlaced()
    {
        var f = new Fixture();

        var preview = await f.Create().PreviewAsync(Buyer, Choices(), CancellationToken.None);

        Assert.True(preview.CanPlace);
        Assert.Empty(preview.Problems);
        Assert.Equal((40m, 8m, 48m), (preview.Subtotal, preview.ShippingTotal, preview.Total));
        Assert.Equal([1, 3], preview.Shops.Select(s => s.Chosen!.RateId));
        Assert.Equal((Address, "cod"), (preview.AddressId, preview.PaymentMethod));
        Assert.Contains(preview.PaymentMethods, m => m.SystemName == "cod");
    }

    [Fact]
    public async Task APreviewWithoutChoicesListsTheProblemsAndNoTotal()
    {
        var f = new Fixture();

        var preview = await f.Create().PreviewAsync(Buyer, new CheckoutChoices(null, null, null), CancellationToken.None);

        Assert.False(preview.CanPlace);
        Assert.Contains(CheckoutProblems.AddressRequired, preview.Problems);
        Assert.Contains(CheckoutProblems.PaymentRequired, preview.Problems);
        Assert.Null(preview.ShippingTotal);
        Assert.Null(preview.Total);
        Assert.Equal(40m, preview.Subtotal);
    }

    [Fact]
    public async Task OptionsAreShownButNeverChosenForTheCustomer()
    {
        var f = new Fixture();

        var preview = await f.Create().PreviewAsync(Buyer, new CheckoutChoices(Address, [], "cod"), CancellationToken.None);

        Assert.Contains(CheckoutProblems.ShippingNotChosen, preview.Problems);
        Assert.Equal([2, 1], preview.Shops.Select(s => s.Options.Count));
        Assert.All(preview.Shops, s => Assert.Null(s.Chosen));
        Assert.Null(preview.Total);
    }

    [Fact]
    public async Task AChoiceThatIsNotAnOptionOrNotAShopOfTheCartIsInvalid()
    {
        var f = new Fixture();

        var notAnOption = await f.Create().PreviewAsync(Buyer, Choices(Address, "cod", new ShippingChoice(Shop, 3), new ShippingChoice(OtherShop, 3)), CancellationToken.None);
        var notAShop = await f.Create().PreviewAsync(Buyer, Choices(Address, "cod", new ShippingChoice(Shop, 1), new ShippingChoice(OtherShop, 3), new ShippingChoice(99, 1)), CancellationToken.None);

        Assert.Contains(CheckoutProblems.ShippingInvalid, notAnOption.Problems);
        Assert.Contains(CheckoutProblems.ShippingInvalid, notAShop.Problems);
    }

    [Fact]
    public async Task AShopThatCannotShipThereIsAProblem()
    {
        var f = new Fixture();
        f.Shipping.Options[OtherShop] = [];

        var preview = await f.Create().PreviewAsync(Buyer, Choices(shipping: [new ShippingChoice(Shop, 1)]), CancellationToken.None);

        Assert.Contains(CheckoutProblems.ShippingUnavailable, preview.Problems);
        Assert.Null(preview.ShippingTotal);
    }

    [Fact]
    public async Task AnAddressOfAnotherCustomerOrAClosedDestinationIsInvalid()
    {
        var f = new Fixture();
        f.Addresses.Addresses.Add(new CustomerAddress { Id = 8, CustomerId = OtherBuyer, CountryCode = "US" });

        var foreign = await f.Create().PreviewAsync(Buyer, Choices(8), CancellationToken.None);
        f.Shipping.DestinationClosed = true;
        var closed = await f.Create().PreviewAsync(Buyer, Choices(), CancellationToken.None);

        Assert.Contains(CheckoutProblems.AddressInvalid, foreign.Problems);
        Assert.Contains(CheckoutProblems.AddressInvalid, closed.Problems);
        Assert.False(closed.CanPlace);
    }

    [Fact]
    public async Task APaymentMethodThatIsOffOrUnknownIsInvalid()
    {
        var f = new Fixture();

        Assert.Contains(CheckoutProblems.PaymentInvalid, (await f.Create().PreviewAsync(Buyer, Choices(method: "sandbox"), CancellationToken.None)).Problems);
        Assert.Contains(CheckoutProblems.PaymentInvalid, (await f.Create().PreviewAsync(Buyer, Choices(method: "paypal"), CancellationToken.None)).Problems);
        Assert.Contains(CheckoutProblems.PaymentRequired, (await f.Create().PreviewAsync(Buyer, Choices(method: " "), CancellationToken.None)).Problems);
    }

    [Fact]
    public async Task CartProblemsShowInThePreview()
    {
        var f = new Fixture();
        f.AddIssue(CartIssues.PriceChanged);
        Assert.Contains(CheckoutProblems.PricesChanged, (await f.Create().PreviewAsync(Buyer, Choices(), CancellationToken.None)).Problems);

        f.AddIssue(CartIssues.OutOfStock);
        Assert.Contains(CheckoutProblems.CartIssues, (await f.Create().PreviewAsync(Buyer, Choices(), CancellationToken.None)).Problems);

        f.Cart.View = new CartView("USD", [], 0, 0, false);
        var empty = await f.Create().PreviewAsync(Buyer, Choices(), CancellationToken.None);
        Assert.Equal(CheckoutProblems.CartEmpty, empty.Problems[0]);
        Assert.Null(empty.ShippingTotal);
    }

    // ---- Place: the whole flow ----

    [Fact]
    public async Task PlacingMakesTheOrderTakesTheStockMakesThePaymentAndEmptiesTheCart()
    {
        var f = new Fixture();

        var result = await Place(f, Request(note: " Leave at the door "));

        Assert.True(result.Succeeded);
        var placed = result.Value!;
        Assert.False(placed.Replayed);

        var order = placed.Order;
        Assert.Equal((40m, 8m, 48m, "cod", "Leave at the door"), (order.Subtotal, order.ShippingTotal, order.Total, order.PaymentMethod, order.CustomerNote));
        Assert.Equal($"{Buyer}:{Key}", order.PlacementKey);
        Assert.Equal(["Mugs Inc", "Tea Co"], order.ShopOrders.Select(s => s.ShopName));
        var first = order.ShopOrders[0];
        Assert.Equal(("Standard", 1, 5m, 20m, 25m), (first.ShippingMethodName, first.ShippingRateId, first.ShippingFee, first.Subtotal, first.Total));
        Assert.Equal((1, 2, 10m, "Product 1", "Red / S", "SKU-1"), (first.Lines[0].ProductId, first.Lines[0].Quantity, first.Lines[0].UnitPrice, first.Lines[0].Name, first.Lines[0].VariantLabel, first.Lines[0].Sku));
        Assert.Equal(("Ann Lee", "+1 555 0100", "1 Main St", "Springfield", "IL", "US"),
            (order.RecipientName, order.RecipientPhone, order.Address1, order.City, order.StateProvince, order.CountryCode));

        // Stock was taken (10 - 2, 10 - 1) and nothing is still held.
        Assert.Equal((8, 9), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Equal(0, f.Stock.ActiveHolds);

        // The payment is for the order total and tied to the order.
        var payment = Assert.Single(f.PaymentStore.Payments);
        Assert.Equal((PaymentReferenceTypes.Order, order.Id, 48m, "cod", PaymentStatus.Pending, Buyer), (payment.ReferenceType, payment.ReferenceId, payment.Amount, payment.Method, payment.Status, payment.CustomerId));
        Assert.Equal($"order-{order.Id}", payment.IdempotencyKey);
        Assert.Equal(PaymentStatus.Pending, placed.Payment!.Status);

        Assert.Equal(1, f.Cart.Cleared);
        Assert.Contains("checkout.placed", f.Audit.Events);
        Assert.Contains("order.created", f.Audit.Events);
    }

    [Fact]
    public async Task TheOrderUsesTheChosenShippingOptionsNotTheCheapest()
    {
        var f = new Fixture();

        var result = await Place(f, Request(shipping: [new ShippingChoice(Shop, 2), new ShippingChoice(OtherShop, 3)]));

        Assert.Equal((12m, 15m, 55m), (result.Value!.Order.ShopOrders[0].ShippingFee, result.Value.Order.ShippingTotal, result.Value.Order.Total));
        Assert.Equal("Express", result.Value.Order.ShopOrders[0].ShippingMethodName);
    }

    [Fact]
    public async Task TheOrderIsPricedWithFreshPricesNotWithWhatTheCartViewSaid()
    {
        var f = new Fixture();
        // The price service changes between the cart view and the placement; the order follows the price service.
        f.Prices.Unit[1] = 11m;

        var result = await Place(f);

        Assert.Equal(11m, result.Value!.Order.ShopOrders[0].Lines[0].UnitPrice);
        Assert.Equal(22m, result.Value.Order.ShopOrders[0].Subtotal);
    }

    // ---- Place: validation ----

    [Theory]
    [InlineData("short")]
    [InlineData("has space 12345")]
    [InlineData(null)]
    public async Task AnInvalidKeyIsRefusedBeforeAnythingElse(string? key)
    {
        var f = new Fixture();

        var result = await Place(f, Request(key: key));

        Assert.Contains("idempotencyKey", result.Errors.Keys);
        Assert.Equal((10, 10), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
    }

    [Fact]
    public async Task ANoteThatIsTooLongIsRefused()
    {
        var f = new Fixture();

        var result = await Place(f, Request(note: new string('x', 501)));

        Assert.Contains("note", result.Errors.Keys);
        Assert.Empty(f.OrderStore.Orders);
    }

    [Fact]
    public async Task TheTermsHaveToBeAccepted()
    {
        var f = new Fixture();

        var result = await Place(f, Request(terms: false));

        Assert.Contains("acceptedTerms", result.Errors.Keys);
        AssertNothingHappened(f);
    }

    [Fact]
    public async Task ChoiceProblemsAreFieldErrorsAndNothingIsTaken()
    {
        var f = new Fixture();

        var noAddress = await Place(f, Request(address: null));
        var foreign = await Place(f, Request(address: 999));
        var noShipping = await Place(f, Request(shipping: [new ShippingChoice(Shop, 1)]));
        var badShipping = await Place(f, Request(shipping: [new ShippingChoice(Shop, 3), new ShippingChoice(OtherShop, 3)]));
        var noMethod = await Place(f, Request(method: null));
        var offMethod = await Place(f, Request(method: "sandbox"));

        Assert.Contains("addressId", noAddress.Errors.Keys);
        Assert.Contains("addressId", foreign.Errors.Keys);
        Assert.Contains("shippingChoices", noShipping.Errors.Keys);
        Assert.Contains("shippingChoices", badShipping.Errors.Keys);
        Assert.Contains("paymentMethod", noMethod.Errors.Keys);
        Assert.Contains("paymentMethod", offMethod.Errors.Keys);
        AssertNothingHappened(f);
    }

    [Fact]
    public async Task SeveralProblemsAreReportedTogether()
    {
        var f = new Fixture();

        var result = await Place(f, Request(address: null, method: null, terms: false));

        Assert.Equal(["acceptedTerms", "addressId", "paymentMethod"], result.Errors.Keys.Order());
    }

    [Fact]
    public async Task ACartThatIsNotReadyIsAConflictAndNothingIsTaken()
    {
        var f = new Fixture();
        f.AddIssue(CartIssues.OutOfStock);
        Assert.Equal(CheckoutErrors.CartNotReady, (await Place(f)).ErrorCode);

        f.Cart.View = new CartView("USD", [], 0, 0, false);
        Assert.Equal(CheckoutErrors.CartNotReady, (await Place(f)).ErrorCode);
        AssertNothingHappened(f, cartCleared: 0);
    }

    [Fact]
    public async Task PricesTheCustomerHasNotAcceptedStopThePlacement()
    {
        var f = new Fixture();
        f.AddIssue(CartIssues.PriceChanged);

        var result = await Place(f);

        Assert.Equal(CheckoutErrors.PricesChanged, result.ErrorCode);
        AssertNothingHappened(f);
    }

    [Fact]
    public async Task CartProblemsComeBeforeChoiceProblems()
    {
        var f = new Fixture();
        f.AddIssue(CartIssues.Unavailable);

        var result = await Place(f, Request(address: null, terms: false));

        Assert.Equal(CheckoutErrors.CartNotReady, result.ErrorCode);
        Assert.Empty(result.Errors);
    }

    // ---- Place: stock ----

    [Fact]
    public async Task NotEnoughStockLeavesNothingTakenNoOrderAndTheCartAsItWas()
    {
        var f = new Fixture();
        f.Stock.OnHand[2] = 0;

        var result = await Place(f);

        Assert.Equal(CatalogErrors.InsufficientStock, result.ErrorCode);
        // Product 1 was held first but nothing was sold, and the hold is gone.
        Assert.Equal((10, 0), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Equal(0, f.Stock.ActiveHolds);
        AssertNothingHappened(f);
    }

    [Fact]
    public async Task ProductsWithoutStockTrackingTakeNothingAndStillSell()
    {
        var f = new Fixture();
        f.Stock.Untracked.Add(1);
        f.Stock.Untracked.Add(2);
        f.Stock.OnHand[1] = 0;

        var result = await Place(f);

        Assert.True(result.Succeeded);
        Assert.Equal((0, 10), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
    }

    [Fact]
    public async Task ALineAboveTheCheckoutLimitIsAFieldErrorAboutTheCart()
    {
        var f = new Fixture();
        f.Stock.OnHand[1] = 5_000;
        f.SetCart((Shop, [(1, 1, CheckoutLimits.MaxLineQuantity + 1)]), (OtherShop, [(2, 2, 1)]));

        var result = await Place(f);

        Assert.Contains("cart", result.Errors.Keys);
        Assert.Equal(5_000, f.Stock.OnHand[1]);
        Assert.Empty(f.OrderStore.Orders);
    }

    // ---- Place: idempotency ----

    [Fact]
    public async Task TheSameKeyReturnsTheSameOrderAndTakesNothingTwice()
    {
        var f = new Fixture();
        var first = await Place(f);
        // The customer refilled the cart in the meantime; a replay must not touch it.
        f.SetCart((Shop, [(1, 1, 3)]));

        var again = await Place(f);

        Assert.True(again.Succeeded);
        Assert.True(again.Value!.Replayed);
        Assert.Equal(first.Value!.Order.Id, again.Value.Order.Id);
        Assert.Equal(first.Value.Payment!.Id, again.Value.Payment!.Id);
        Assert.Single(f.OrderStore.Orders);
        Assert.Single(f.PaymentStore.Payments);
        Assert.Equal((8, 9), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Equal(1, f.Cart.Cleared);
        Assert.Single(f.Audit.Events, e => e == "checkout.placed");
    }

    [Fact]
    public async Task TheSameKeyWithAnotherPaymentMethodIsAConflict()
    {
        var f = new Fixture();
        await Place(f);

        var result = await Place(f, Request(method: "flaky"));

        Assert.Equal(OrderErrors.PlacementConflict, result.ErrorCode);
        Assert.Single(f.OrderStore.Orders);
    }

    [Fact]
    public async Task TheSameKeyOfAnotherCustomerIsAnotherOrder()
    {
        var f = new Fixture();
        var mine = await Place(f);
        f.Addresses.Addresses.Add(new CustomerAddress { Id = 9, CustomerId = OtherBuyer, FirstName = "Bo", LastName = "Kim", Address1 = "2 Side St", City = "Town", CountryCode = "US", PhoneNumber = "1" });
        // The first checkout emptied the cart; the other customer fills theirs the same way.
        f.SetCart((Shop, [(1, 1, 2)]), (OtherShop, [(2, 2, 1)]));
        f.CartLines.Lines.ForEach(l => l.CustomerId = OtherBuyer);

        var theirs = await f.Create().PlaceAsync(OtherBuyer, Request(address: 9), CancellationToken.None);

        Assert.True(theirs.Succeeded);
        Assert.False(theirs.Value!.Replayed);
        Assert.NotEqual(mine.Value!.Order.Id, theirs.Value.Order.Id);
        Assert.Equal($"{OtherBuyer}:{Key}", theirs.Value.Order.PlacementKey);
    }

    // ---- Place: failures after the stock is taken ----

    [Fact]
    public async Task AnOrderThatCannotBeMadeGivesTheStockBack()
    {
        var f = new Fixture();
        // The shop disappears after the cart was checked: the order service refuses it.
        f.Vendors.Vendors.RemoveAll(v => v.Id == OtherShop);

        var result = await Place(f);

        Assert.False(result.Succeeded);
        Assert.Equal((10, 10), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Equal(2, f.Stock.Returns.Count);
        Assert.All(f.Stock.Returns, r => Assert.StartsWith("checkout-rollback:", r.Reference));
        AssertNothingHappened(f, expectReturns: true);
    }

    [Fact]
    public async Task AFailedPaymentCancelsTheOrderGivesTheStockBackAndKeepsTheCart()
    {
        var f = new Fixture();
        f.Flaky.FailInitiate = true;

        var result = await Place(f, Request(method: "flaky"));

        Assert.Equal(CheckoutErrors.PaymentFailed, result.ErrorCode);
        var order = Assert.Single(f.OrderStore.Orders);
        Assert.All(order.ShopOrders, s => Assert.Equal(ShopOrderStatus.Cancelled, s.Status));
        Assert.All(order.ShopOrders, s => Assert.Equal("Payment failed", s.CancelReason));
        Assert.Equal((10, 10), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Equal(0, f.Cart.Cleared);
        Assert.NotEmpty(f.Cart.View.Groups);
        Assert.DoesNotContain("checkout.placed", f.Audit.Events);
    }

    [Fact]
    public async Task ASecondTryAfterAFailedPaymentUsesANewKeyAndWorks()
    {
        var f = new Fixture();
        f.Flaky.FailInitiate = true;
        await Place(f, Request(method: "flaky"));
        f.Flaky.FailInitiate = false;

        var retry = await Place(f, Request(method: "flaky", key: "another-key-1234"));

        Assert.True(retry.Succeeded);
        Assert.Equal(PaymentStatus.Authorized, retry.Value!.Payment!.Status);
        Assert.Equal((8, 9), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Equal(1, f.Cart.Cleared);
    }

    private static void AssertNothingHappened(Fixture f, int cartCleared = 0, bool expectReturns = false)
    {
        Assert.Equal(cartCleared, f.Cart.Cleared);
        Assert.Empty(f.PaymentStore.Payments);
        Assert.Equal(0, f.Stock.ActiveHolds);
        Assert.DoesNotContain("checkout.placed", f.Audit.Events);
        if (!expectReturns) Assert.Empty(f.OrderStore.Orders);
    }

    // ---- Discount codes ----

    [Fact]
    public async Task APlatformCodeCutsTheItemsInThePreviewAndSplitsAcrossTheShops()
    {
        var f = new Fixture();
        f.Code();

        var preview = await f.Create().PreviewAsync(Buyer, WithCode("welcome10"), CancellationToken.None);

        Assert.True(preview.CanPlace);
        Assert.Equal(("WELCOME10", DiscountFunding.Platform, 4m), (preview.Discount!.Code, preview.Discount.Funding, preview.Discount.Amount));
        Assert.Equal((2m, 2m), (preview.Discount.Split[Shop], preview.Discount.Split[OtherShop]));
        // Items 40, minus 4, plus shipping 8; shipping is never discounted.
        Assert.Equal((40m, 8m, 44m), (preview.Subtotal, preview.ShippingTotal, preview.Total));
        Assert.Null(preview.CouponReason);
    }

    [Fact]
    public async Task ACodeThatDoesNotApplyBlocksPlacingAndSaysWhy()
    {
        var f = new Fixture();
        var discount = f.Code();
        discount.EndsOnUtc = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var expired = await f.Create().PreviewAsync(Buyer, WithCode("WELCOME10"), CancellationToken.None);
        var unknown = await f.Create().PreviewAsync(Buyer, WithCode("NOPE"), CancellationToken.None);

        Assert.Contains(CheckoutProblems.CouponInvalid, expired.Problems);
        Assert.False(expired.CanPlace);
        Assert.Equal(DiscountReasons.Expired, expired.CouponReason);
        Assert.Null(expired.Discount);
        Assert.Equal(DiscountReasons.NotFound, unknown.CouponReason);
        // Without a discount the total is what it would be without the code.
        Assert.Equal(48m, expired.Total);
    }

    [Fact]
    public async Task NoCodeMeansNoDiscountAndNoProblem()
    {
        var f = new Fixture();
        f.Code();

        var preview = await f.Create().PreviewAsync(Buyer, WithCode("  "), CancellationToken.None);

        Assert.Null(preview.Discount);
        Assert.DoesNotContain(CheckoutProblems.CouponInvalid, preview.Problems);
        Assert.Equal(48m, preview.Total);
    }

    [Fact]
    public async Task PlacingWithAPlatformCodeRecordsTheSplitTheFundingAndThePaymentOfTheDiscountedTotal()
    {
        var f = new Fixture();
        var discount = f.Code();

        var result = await Place(f, Request(code: "welcome10"));

        Assert.True(result.Succeeded);
        var order = result.Value!.Order;
        Assert.Equal(("WELCOME10", 4m, 44m), (order.DiscountCode, order.DiscountTotal, order.Total));
        Assert.Equal((2m, 2m), (order.ShopOrders[0].DiscountAmount, order.ShopOrders[1].DiscountAmount));
        Assert.All(order.ShopOrders, s => Assert.Equal("platform", s.DiscountFunding));
        Assert.Equal((23m, 21m), (order.ShopOrders[0].Total, order.ShopOrders[1].Total));

        Assert.Equal(44m, Assert.Single(f.PaymentStore.Payments).Amount);
        Assert.Equal(1, discount.UsedCount);
        Assert.Equal(order.Id, Assert.Single(f.Discounts.Usages).OrderId);
        Assert.Contains("discount.redeemed", f.Audit.Events);
    }

    [Fact]
    public async Task AShopCodeDiscountsOnlyThatShopAndRecordsTheShopAsFunder()
    {
        var f = new Fixture();
        f.Code("SHOP5", "fixed", 5, vendorId: Shop);

        var result = await Place(f, Request(code: "SHOP5"));

        var order = result.Value!.Order;
        Assert.Equal((5m, 43m), (order.DiscountTotal, order.Total));
        Assert.Equal((5m, "shop", 20m), (order.ShopOrders[0].DiscountAmount, order.ShopOrders[0].DiscountFunding, order.ShopOrders[0].Subtotal));
        Assert.Equal((0m, null), (order.ShopOrders[1].DiscountAmount, order.ShopOrders[1].DiscountFunding));
    }

    [Fact]
    public async Task AShopCodeDoesNotApplyWhenItsShopIsNotInTheCart()
    {
        var f = new Fixture();
        f.Code("SHOP5", "fixed", 5, vendorId: 77);

        var result = await Place(f, Request(code: "SHOP5"));

        Assert.Contains("couponCode", result.Errors.Keys);
        AssertNothingHappened(f);
    }

    [Fact]
    public async Task ACodeThatDoesNotApplyIsAFieldErrorWithTheReasonAndNothingIsTaken()
    {
        var f = new Fixture();
        f.Code("SMALL", "fixed", 5).MinSubtotal = 1000;

        var result = await Place(f, Request(code: "SMALL"));

        Assert.Equal(DiscountRules.MessageOf(DiscountReasons.MinSubtotal), result.Errors["couponCode"][0]);
        AssertNothingHappened(f);
        Assert.Empty(f.Discounts.Usages);
    }

    [Fact]
    public async Task TheLastUseTakenMeanwhileCancelsTheOrderAndGivesTheStockBack()
    {
        var f = new Fixture();
        var discount = f.Code(maxUses: 1);
        // Someone else takes the last use between the check and the redeem.
        f.Discounts.BeforeRedeem = () => discount.UsedCount = 1;

        var result = await Place(f, Request(code: "WELCOME10"));

        Assert.Equal(CheckoutErrors.CouponUnavailable, result.ErrorCode);
        var order = Assert.Single(f.OrderStore.Orders);
        Assert.All(order.ShopOrders, s => Assert.Equal(ShopOrderStatus.Cancelled, s.Status));
        Assert.Equal((10, 10), (f.Stock.OnHand[1], f.Stock.OnHand[2]));
        Assert.Empty(f.PaymentStore.Payments);
        Assert.Equal(0, f.Cart.Cleared);
        Assert.Empty(f.Discounts.Usages);
    }

    [Fact]
    public async Task AFailedPaymentGivesTheUseOfTheCodeBack()
    {
        var f = new Fixture();
        var discount = f.Code(maxUses: 1);
        f.Flaky.FailInitiate = true;

        var result = await Place(f, Request(method: "flaky", code: "WELCOME10"));

        Assert.Equal(CheckoutErrors.PaymentFailed, result.ErrorCode);
        Assert.Equal(0, discount.UsedCount);
        Assert.Empty(f.Discounts.Usages);
        Assert.Contains("discount.released", f.Audit.Events);

        // The code can be used again by the next, successful try.
        f.Flaky.FailInitiate = false;
        Assert.True((await Place(f, Request(method: "flaky", code: "WELCOME10", key: "another-key-1234"))).Succeeded);
        Assert.Equal(1, discount.UsedCount);
    }

    [Fact]
    public async Task ReplayingAPlacementDoesNotUseTheCodeTwice()
    {
        var f = new Fixture();
        var discount = f.Code();

        await Place(f, Request(code: "WELCOME10"));
        var again = await Place(f, Request(code: "WELCOME10"));

        Assert.True(again.Value!.Replayed);
        Assert.Equal(1, discount.UsedCount);
        Assert.Single(f.Discounts.Usages);
    }

    [Fact]
    public async Task ThePerCustomerLimitStopsTheSecondOrder()
    {
        var f = new Fixture();
        f.Code(perCustomer: 1);
        await Place(f, Request(code: "WELCOME10"));
        f.SetCart((Shop, [(1, 1, 2)]), (OtherShop, [(2, 2, 1)]));

        var second = await Place(f, Request(code: "WELCOME10", key: "second-key-1234"));

        Assert.Equal(DiscountRules.MessageOf(DiscountReasons.CustomerLimitReached), second.Errors["couponCode"][0]);
        Assert.Single(f.OrderStore.Orders);
    }
}
