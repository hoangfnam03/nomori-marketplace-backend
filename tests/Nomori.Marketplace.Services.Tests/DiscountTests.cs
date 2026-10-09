using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Services.Discounts;

namespace Nomori.Marketplace.Services.Tests;

public sealed class DiscountTests
{
    private const int Shop = 5;
    private const int OtherShop = 6;
    private const int Buyer = 100;
    private const int Admin = 1;

    // The test clock is 2026-01-01 00:00 UTC.
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>In-memory discounts with the same rules as the database: unique code, usage limits checked together with the write.</summary>
    internal sealed class FakeDiscountStore : IDiscountStore
    {
        private int nextId = 1;

        public List<Discount> Discounts { get; } = [];
        public List<(int DiscountId, int? OrderId, int CustomerId, decimal Amount)> Usages { get; } = [];

        /// <summary>Runs just before a redeem is checked, so a test can play another buyer who got there first.</summary>
        public Action? BeforeRedeem { get; set; }

        public Task<IReadOnlyList<Discount>> GetListAsync(int? vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Discount>>(Discounts.Where(d => d.VendorId == vendorId).OrderByDescending(d => d.Id).ToList());

        public Task<int> CountAsync(int? vendorId, CancellationToken cancellationToken) => Task.FromResult(Discounts.Count(d => d.VendorId == vendorId));
        public Task<Discount?> GetAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Discounts.FirstOrDefault(d => d.Id == id));
        public Task<Discount?> GetByCodeAsync(string code, CancellationToken cancellationToken) => Task.FromResult(Discounts.FirstOrDefault(d => d.Code == code));

        public Task<int> InsertAsync(Discount discount, CancellationToken cancellationToken)
        {
            if (Discounts.Any(d => d.Code == discount.Code)) return Task.FromResult(0);
            discount.Id = nextId++;
            Discounts.Add(discount);
            return Task.FromResult(discount.Id);
        }

        public Task<bool> UpdateAsync(Discount discount, CancellationToken cancellationToken) =>
            Task.FromResult(!Discounts.Any(d => d.Id != discount.Id && d.Code == discount.Code));

        public Task<bool> HasUsageAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Usages.Any(u => u.DiscountId == id));

        public Task DeleteAsync(int id, CancellationToken cancellationToken)
        {
            Discounts.RemoveAll(d => d.Id == id);
            return Task.CompletedTask;
        }

        public Task<int> CountCustomerUsesAsync(int discountId, int customerId, CancellationToken cancellationToken) =>
            Task.FromResult(Usages.Count(u => u.DiscountId == discountId && u.CustomerId == customerId));

        public Task<IReadOnlyList<Discount>> GetOffersAsync(IReadOnlyCollection<int> vendorIds, DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Discount>>(Discounts.Where(d => DiscountRules.IsOffered(d, nowUtc, vendorIds))
                .OrderByDescending(d => d.Id).Take(DiscountLimits.MaxOffers).ToList());

        public Task<IReadOnlyDictionary<int, int>> CountCustomerUsesAsync(IReadOnlyCollection<int> discountIds, int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, int>>(Usages.Where(u => u.CustomerId == customerId && discountIds.Contains(u.DiscountId))
                .GroupBy(u => u.DiscountId).ToDictionary(g => g.Key, g => g.Count()));

        public Task<RedeemOutcome> TryRedeemAsync(RedeemRequest request, CancellationToken cancellationToken)
        {
            BeforeRedeem?.Invoke();
            var discount = Discounts.FirstOrDefault(d => d.Id == request.DiscountId);
            if (discount is null || !discount.Enabled) return Task.FromResult(RedeemOutcome.Unavailable);
            if (discount.MaxUses is { } total && discount.UsedCount >= total) return Task.FromResult(RedeemOutcome.LimitReached);
            if (discount.MaxUsesPerCustomer is { } each && Usages.Count(u => u.DiscountId == discount.Id && u.CustomerId == request.CustomerId) >= each)
                return Task.FromResult(RedeemOutcome.CustomerLimitReached);
            if (Usages.Any(u => u.OrderId == request.OrderId)) return Task.FromResult(RedeemOutcome.Unavailable);

            Usages.Add((discount.Id, request.OrderId, request.CustomerId, request.Amount));
            discount.UsedCount++;
            return Task.FromResult(RedeemOutcome.Redeemed);
        }

        public Task<bool> ReleaseAsync(int orderId, CancellationToken cancellationToken)
        {
            var usage = Usages.FirstOrDefault(u => u.OrderId == orderId);
            if (usage.DiscountId == 0) return Task.FromResult(false);
            Usages.Remove(usage);
            Discounts.Single(d => d.Id == usage.DiscountId).UsedCount--;
            return Task.FromResult(true);
        }
    }

    private sealed class Fixture
    {
        public FakeDiscountStore Store { get; } = new();
        public RecordingAuditLog Audit { get; } = new();

        public DiscountService Create(int decimals = 2) => new(Store, new FakePrimaryCurrency(decimals), Audit, new TestClock());

        public async Task<Discount> MakeAsync(int? vendorId = null, string code = "WELCOME10", string type = "percentage", decimal value = 10, int? maxUses = null,
            int? perCustomer = null, decimal? minSubtotal = null, bool enabled = true)
        {
            var result = await Create().CreateAsync(vendorId, Command(code: code, type: type, value: value, maxUses: maxUses, perCustomer: perCustomer,
                minSubtotal: minSubtotal, enabled: enabled), Admin, CancellationToken.None);
            return result.Value!;
        }
    }

    private static SaveDiscountCommand Command(
        string? name = "Welcome", string? code = "welcome10", string? type = "percentage", decimal value = 10, decimal? cap = null,
        DateTime? starts = null, DateTime? ends = null, decimal? minSubtotal = null, int? maxUses = null, int? perCustomer = null, bool enabled = true) =>
        new(name, code, type, value, cap, starts, ends, minSubtotal, maxUses, perCustomer, enabled);

    private static Discount Rule(DiscountType type = DiscountType.Percentage, decimal value = 10, int? vendorId = null) =>
        new() { Id = 1, VendorId = vendorId, Name = "d", Code = "D", Type = type, Value = value, Enabled = true };

    private static Dictionary<int, decimal> Cart(params (int Vendor, decimal Subtotal)[] shops) => shops.ToDictionary(s => s.Vendor, s => s.Subtotal);

    // ---- Pure rules: amount ----

    [Theory]
    [InlineData(DiscountType.Percentage, 10, 40, 4)]
    [InlineData(DiscountType.Percentage, 33.33, 10, 3.33)]
    [InlineData(DiscountType.Percentage, 100, 40, 40)]
    [InlineData(DiscountType.Fixed, 5, 40, 5)]
    [InlineData(DiscountType.Fixed, 50, 40, 40)]
    public void TheAmountIsAPercentageOrAFixedAmountNeverAboveTheSubtotal(DiscountType type, double value, double subtotal, double expected) =>
        Assert.Equal((decimal)expected, DiscountRules.AmountFor(Rule(type, (decimal)value), (decimal)subtotal, 2));

    [Fact]
    public void ACapLimitsAPercentageButNotAFixedAmount()
    {
        var percentage = Rule(value: 50);
        percentage.MaxDiscountAmount = 3;
        Assert.Equal(3m, DiscountRules.AmountFor(percentage, 40, 2));

        var fixedAmount = Rule(DiscountType.Fixed, 8);
        fixedAmount.MaxDiscountAmount = 3;
        Assert.Equal(8m, DiscountRules.AmountFor(fixedAmount, 40, 2));
    }

    [Fact]
    public void PercentagesRoundToTheCurrencyAwayFromZero()
    {
        Assert.Equal(0.67m, DiscountRules.AmountFor(Rule(value: 10), 6.65m, 2));
        Assert.Equal(1m, DiscountRules.AmountFor(Rule(value: 10), 5m, 0));
    }

    // ---- Pure rules: who it applies to ----

    [Fact]
    public void APlatformDiscountLooksAtTheWholeCart()
    {
        var check = DiscountRules.Evaluate(Rule(), 0, Now, Cart((Shop, 20), (OtherShop, 20)), 2);

        Assert.Equal(4m, check.Applied!.Amount);
        Assert.Equal(DiscountFunding.Platform, check.Applied.Discount.Funding);
    }

    [Fact]
    public void AShopDiscountLooksOnlyAtItsOwnShop()
    {
        var check = DiscountRules.Evaluate(Rule(vendorId: Shop), 0, Now, Cart((Shop, 20), (OtherShop, 100)), 2);

        Assert.Equal(2m, check.Applied!.Amount);
        Assert.Equal(DiscountFunding.Shop, check.Applied.Discount.Funding);
        Assert.Equal([Shop], check.Applied.Split.Keys);
    }

    [Fact]
    public void AShopDiscountWhoseShopIsNotInTheCartDoesNotExist()
    {
        var check = DiscountRules.Evaluate(Rule(vendorId: Shop), 0, Now, Cart((OtherShop, 100)), 2);

        Assert.Null(check.Applied);
        Assert.Equal(DiscountReasons.NotFound, check.Reason);
    }

    // ---- Pure rules: reasons ----

    [Fact]
    public void TheReasonsAreCheckedInOrder()
    {
        var d = Rule();
        d.Enabled = false;
        d.StartsOnUtc = Now.AddDays(1);
        d.EndsOnUtc = Now.AddDays(2);
        d.MinSubtotal = 1000;
        d.MaxUses = 1;
        d.UsedCount = 1;
        d.MaxUsesPerCustomer = 1;
        var cart = Cart((Shop, 20));

        string? Reason() => DiscountRules.Evaluate(d, 1, Now, cart, 2).Reason;

        Assert.Equal(DiscountReasons.Disabled, Reason());
        d.Enabled = true;
        Assert.Equal(DiscountReasons.NotStarted, Reason());
        d.StartsOnUtc = null;
        d.EndsOnUtc = Now;
        Assert.Equal(DiscountReasons.Expired, Reason());
        d.EndsOnUtc = null;
        Assert.Equal(DiscountReasons.MinSubtotal, Reason());
        d.MinSubtotal = null;
        Assert.Equal(DiscountReasons.LimitReached, Reason());
        d.MaxUses = null;
        Assert.Equal(DiscountReasons.CustomerLimitReached, Reason());
        d.MaxUsesPerCustomer = null;
        Assert.Null(Reason());
    }

    [Fact]
    public void TheStartIsInclusiveAndTheEndIsExclusive()
    {
        var d = Rule();
        d.StartsOnUtc = Now;
        d.EndsOnUtc = Now.AddHours(1);

        Assert.NotNull(DiscountRules.Evaluate(d, 0, Now, Cart((Shop, 20)), 2).Applied);
        Assert.NotNull(DiscountRules.Evaluate(d, 0, Now.AddMinutes(59), Cart((Shop, 20)), 2).Applied);
        Assert.Equal(DiscountReasons.Expired, DiscountRules.Evaluate(d, 0, Now.AddHours(1), Cart((Shop, 20)), 2).Reason);
        Assert.Equal(DiscountReasons.NotStarted, DiscountRules.Evaluate(d, 0, Now.AddSeconds(-1), Cart((Shop, 20)), 2).Reason);
    }

    [Fact]
    public void TheMinimumLooksAtTheEligibleSubtotalOnly()
    {
        var shopRule = Rule(vendorId: Shop);
        shopRule.MinSubtotal = 50;

        // The cart is above 50 in total but this shop sold only 20.
        Assert.Equal(DiscountReasons.MinSubtotal, DiscountRules.Evaluate(shopRule, 0, Now, Cart((Shop, 20), (OtherShop, 100)), 2).Reason);
        // Exactly the minimum is enough.
        Assert.NotNull(DiscountRules.Evaluate(shopRule, 0, Now, Cart((Shop, 50)), 2).Applied);
    }

    [Fact]
    public void ADiscountThatRoundsToNothingIsRefused()
    {
        var check = DiscountRules.Evaluate(Rule(value: 0.01m), 0, Now, Cart((Shop, 1m)), 2);

        Assert.Equal(DiscountReasons.NothingToDiscount, check.Reason);
    }

    // ---- Pure rules: split ----

    [Fact]
    public void ASplitIsProportionalToTheSubtotals()
    {
        var split = DiscountRules.Split(6m, Cart((Shop, 20), (OtherShop, 40)), 2);

        Assert.Equal((2m, 4m), (split[Shop], split[OtherShop]));
    }

    [Fact]
    public void ASplitAlwaysAddsUpAndTheLeftoverGoesToTheLargestShop()
    {
        var thirds = DiscountRules.Split(10m, Cart((1, 10), (2, 10), (3, 10)), 2);

        Assert.Equal(10m, thirds.Values.Sum());
        // Ties go to the lowest id: it takes the extra cent.
        Assert.Equal((3.34m, 3.33m, 3.33m), (thirds[1], thirds[2], thirds[3]));

        var uneven = DiscountRules.Split(1m, Cart((1, 3), (2, 7), (3, 90)), 2);
        Assert.Equal(1m, uneven.Values.Sum());
        Assert.All(uneven, p => Assert.True(p.Value > 0));
    }

    [Fact]
    public void APlatformDiscountOfTheWholeCartTakesEverySubtotal()
    {
        var d = Rule(DiscountType.Fixed, 100);

        var check = DiscountRules.Evaluate(d, 0, Now, Cart((Shop, 20), (OtherShop, 40)), 2);

        Assert.Equal(60m, check.Applied!.Amount);
        Assert.Equal((20m, 40m), (check.Applied.Split[Shop], check.Applied.Split[OtherShop]));
    }

    [Fact]
    public void CodesAreNormalisedAndChecked()
    {
        Assert.Equal("WELCOME10", DiscountRules.NormalizeCode("  welcome10 "));
        Assert.Equal(string.Empty, DiscountRules.NormalizeCode(null));
        Assert.True(DiscountRules.IsValidCode("A-B_9"));
        Assert.False(DiscountRules.IsValidCode("AB"));
        Assert.False(DiscountRules.IsValidCode("HAS SPACE"));
        Assert.False(DiscountRules.IsValidCode("lower"));
        Assert.False(DiscountRules.IsValidCode(new string('A', DiscountLimits.MaxCodeLength + 1)));
        Assert.All(new[] { DiscountReasons.Disabled, DiscountReasons.Expired, DiscountReasons.NotFound, DiscountReasons.LimitReached }, r => Assert.NotEmpty(DiscountRules.MessageOf(r)));
    }

    // ---- Service: scopes ----

    [Fact]
    public async Task ADiscountIsCreatedInItsScopeWithANormalisedCodeAndAudited()
    {
        var f = new Fixture();

        var platform = await f.Create().CreateAsync(null, Command(name: " Welcome "), Admin, CancellationToken.None);
        var shop = await f.Create().CreateAsync(Shop, Command(code: "shop5", type: "fixed", value: 5), Admin, CancellationToken.None);

        Assert.True(platform.Succeeded);
        Assert.Equal(("WELCOME10", "Welcome", null, DiscountFunding.Platform), (platform.Value!.Code, platform.Value.Name, platform.Value.VendorId, platform.Value.Funding));
        Assert.Equal((Shop, DiscountType.Fixed, DiscountFunding.Shop), (shop.Value!.VendorId, shop.Value.Type, shop.Value.Funding));
        Assert.Equal(2, f.Audit.Events.Count(e => e == "discount.created"));
        Assert.Single(await f.Create().GetListAsync(null, CancellationToken.None));
        Assert.Single(await f.Create().GetListAsync(Shop, CancellationToken.None));
        Assert.Empty(await f.Create().GetListAsync(OtherShop, CancellationToken.None));
    }

    [Theory]
    [InlineData("", "WELCOME10", "percentage", 10, "name")]
    [InlineData("n", "AB", "percentage", 10, "code")]
    [InlineData("n", "HAS SPACE", "percentage", 10, "code")]
    [InlineData("n", "WELCOME10", "weird", 10, "type")]
    [InlineData("n", "WELCOME10", "percentage", 0, "value")]
    [InlineData("n", "WELCOME10", "percentage", 100.5, "value")]
    [InlineData("n", "WELCOME10", "percentage", 10.123, "value")]
    [InlineData("n", "WELCOME10", "fixed", 0, "value")]
    [InlineData("n", "WELCOME10", "fixed", 5.123, "value")]
    public async Task InvalidDiscountsAreRefusedWithTheFieldName(string name, string code, string type, double value, string field)
    {
        var f = new Fixture();

        var result = await f.Create().CreateAsync(null, Command(name, code, type, (decimal)value), Admin, CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
        Assert.Empty(f.Store.Discounts);
    }

    [Fact]
    public async Task TheOtherFieldsAreChecked()
    {
        var f = new Fixture();
        var s = f.Create();

        Assert.Contains("endsOnUtc", (await s.CreateAsync(null, Command(starts: Now, ends: Now), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("minSubtotal", (await s.CreateAsync(null, Command(minSubtotal: 0), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("maxUses", (await s.CreateAsync(null, Command(maxUses: 0), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("maxUsesPerCustomer", (await s.CreateAsync(null, Command(perCustomer: 1_000_001), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("maxDiscountAmount", (await s.CreateAsync(null, Command(type: "fixed", value: 5, cap: 3), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("maxDiscountAmount", (await s.CreateAsync(null, Command(cap: 0), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Contains("name", (await s.CreateAsync(null, Command(name: new string('x', 101)), Admin, CancellationToken.None)).Errors.Keys);
        Assert.Empty(f.Store.Discounts);
    }

    [Fact]
    public async Task AmountsFollowTheDecimalsOfTheCurrency()
    {
        var f = new Fixture();

        var result = await f.Create(decimals: 0).CreateAsync(null, Command(type: "fixed", value: 4.5m, minSubtotal: 1.5m), Admin, CancellationToken.None);

        Assert.Contains("value", result.Errors.Keys);
        Assert.Contains("minSubtotal", result.Errors.Keys);
    }

    [Fact]
    public async Task ACodeIsUniqueAcrossTheWholePlatform()
    {
        var f = new Fixture();
        await f.MakeAsync(null, "SAVE");

        Assert.Equal(DiscountErrors.CodeExists, (await f.Create().CreateAsync(Shop, Command(code: "save"), Admin, CancellationToken.None)).ErrorCode);
        Assert.Single(f.Store.Discounts);
    }

    [Fact]
    public async Task EachScopeHasItsOwnLimit()
    {
        var f = new Fixture();
        for (var i = 0; i < DiscountLimits.MaxPerShop; i++)
            f.Store.Discounts.Add(new Discount { Id = 1000 + i, VendorId = Shop, Code = "C" + i });

        Assert.Equal(DiscountErrors.Limit, (await f.Create().CreateAsync(Shop, Command(), Admin, CancellationToken.None)).ErrorCode);
        Assert.True((await f.Create().CreateAsync(OtherShop, Command(), Admin, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task ADiscountOfAnotherScopeIsNotFoundForEveryAction()
    {
        var f = new Fixture();
        var theirs = await f.MakeAsync(OtherShop, "THEIRS");
        var platform = await f.MakeAsync(null, "PLATFORM");

        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateAsync(Shop, theirs.Id, Command(), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeleteAsync(Shop, theirs.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateAsync(Shop, platform.Id, Command(), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().UpdateAsync(null, theirs.Id, Command(), Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await f.Create().DeleteAsync(null, 999, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(2, f.Store.Discounts.Count);
    }

    [Fact]
    public async Task UpdatingKeepsTheOwnerAndTheUseCount()
    {
        var f = new Fixture();
        var discount = await f.MakeAsync(Shop, "SHOP5");
        discount.UsedCount = 3;

        var result = await f.Create().UpdateAsync(Shop, discount.Id, Command(name: "New name", code: "shop5", type: "fixed", value: 7, enabled: false), Admin, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(("New name", DiscountType.Fixed, 7m, false, Shop, 3), (discount.Name, discount.Type, discount.Value, discount.Enabled, discount.VendorId, discount.UsedCount));
        Assert.Contains("discount.updated", f.Audit.Events);
    }

    [Fact]
    public async Task ACapIsDroppedWhenTheTypeBecomesFixed()
    {
        var f = new Fixture();
        var discount = (await f.Create().CreateAsync(null, Command(cap: 3), Admin, CancellationToken.None)).Value!;
        Assert.Equal(3m, discount.MaxDiscountAmount);

        await f.Create().UpdateAsync(null, discount.Id, Command(type: "fixed", value: 5), Admin, CancellationToken.None);

        Assert.Null(discount.MaxDiscountAmount);
    }

    [Fact]
    public async Task ACodeCannotBeChangedToOneThatAnotherDiscountHas()
    {
        var f = new Fixture();
        await f.MakeAsync(null, "FIRST");
        var second = await f.MakeAsync(null, "SECOND");

        var result = await f.Create().UpdateAsync(null, second.Id, Command(code: "first"), Admin, CancellationToken.None);

        Assert.Equal(DiscountErrors.CodeExists, result.ErrorCode);
    }

    [Fact]
    public async Task AnUnusedDiscountCanBeDeletedAndAUsedOneCannot()
    {
        var f = new Fixture();
        var unused = await f.MakeAsync(null, "UNUSED");
        var used = await f.MakeAsync(null, "USED");
        used.UsedCount = 1;
        var released = await f.MakeAsync(null, "RELEASED");
        f.Store.Usages.Add((released.Id, 1, Buyer, 1m));

        Assert.True((await f.Create().DeleteAsync(null, unused.Id, Admin, CancellationToken.None)).Succeeded);
        Assert.Equal(DiscountErrors.InUse, (await f.Create().DeleteAsync(null, used.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(DiscountErrors.InUse, (await f.Create().DeleteAsync(null, released.Id, Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(2, f.Store.Discounts.Count);
        Assert.Single(f.Audit.Events, e => e == "discount.deleted");
    }

    // ---- Service: checkout ----

    [Fact]
    public async Task AnUnknownOrMalformedCodeDoesNotExist()
    {
        var f = new Fixture();
        await f.MakeAsync();

        Assert.Equal(DiscountReasons.NotFound, (await f.Create().CheckCouponAsync("NOPE", Buyer, Cart((Shop, 20)), CancellationToken.None)).Reason);
        Assert.Equal(DiscountReasons.NotFound, (await f.Create().CheckCouponAsync("a b", Buyer, Cart((Shop, 20)), CancellationToken.None)).Reason);
        Assert.Equal(DiscountReasons.NotFound, (await f.Create().CheckCouponAsync(null, Buyer, Cart((Shop, 20)), CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task ACodeIsFoundWhateverTheCase()
    {
        var f = new Fixture();
        await f.MakeAsync();

        var check = await f.Create().CheckCouponAsync(" welcome10 ", Buyer, Cart((Shop, 20), (OtherShop, 20)), CancellationToken.None);

        Assert.Equal(4m, check.Applied!.Amount);
        Assert.Empty(f.Store.Usages);
    }

    [Fact]
    public async Task ThePerCustomerLimitCountsThatCustomersUses()
    {
        var f = new Fixture();
        var discount = await f.MakeAsync(perCustomer: 1);
        f.Store.Usages.Add((discount.Id, 1, Buyer, 1m));

        Assert.Equal(DiscountReasons.CustomerLimitReached, (await f.Create().CheckCouponAsync("WELCOME10", Buyer, Cart((Shop, 20)), CancellationToken.None)).Reason);
        Assert.NotNull((await f.Create().CheckCouponAsync("WELCOME10", Buyer + 1, Cart((Shop, 20)), CancellationToken.None)).Applied);
    }

    // ---- Pure rules and service: codes offered at checkout ----

    [Fact]
    public void ACodeIsOfferedWhenOnNotOverAndThePlatformsOrAShopsOfTheCart()
    {
        int[] shops = [Shop];

        Assert.True(DiscountRules.IsOffered(Rule(), Now, shops));
        Assert.True(DiscountRules.IsOffered(Rule(vendorId: Shop), Now, shops));
        Assert.False(DiscountRules.IsOffered(Rule(vendorId: OtherShop), Now, shops));
        Assert.False(DiscountRules.IsOffered(new Discount { Code = "OFF", Enabled = false }, Now, shops));
        Assert.False(DiscountRules.IsOffered(new Discount { Code = "OVER", Enabled = true, EndsOnUtc = Now }, Now, shops));
        // Not started yet: offered, so the customer sees it coming (disabled).
        Assert.True(DiscountRules.IsOffered(new Discount { Code = "SOON", Enabled = true, StartsOnUtc = Now.AddDays(1) }, Now, shops));
    }

    [Fact]
    public void AnOfferGivesTheAmountOrTheReasonAndWhatIsMissingForTheMinimum()
    {
        var usable = DiscountRules.Offer(Rule(value: 10), 0, Now, Cart((Shop, 40m)), 2);
        Assert.Equal((4m, (string?)null, (decimal?)null), (usable.Amount, usable.Reason, usable.Shortfall));

        var shopCode = Rule(vendorId: Shop);
        shopCode.MinSubtotal = 50m;
        var below = DiscountRules.Offer(shopCode, 0, Now, Cart((Shop, 30m), (OtherShop, 100m)), 2);
        // Only the shop's own lines count toward its minimum.
        Assert.Equal(((decimal?)null, DiscountReasons.MinSubtotal, (decimal?)20m), (below.Amount, below.Reason, below.Shortfall));

        var used = Rule();
        used.MaxUsesPerCustomer = 1;
        Assert.Equal(DiscountReasons.CustomerLimitReached, DiscountRules.Offer(used, 1, Now, Cart((Shop, 40m)), 2).Reason);
    }

    [Fact]
    public void UsableOffersComeFirstBiggestFirstThenTheClosestToUsable()
    {
        static CouponOffer O(string code, decimal? amount, decimal? shortfall = null) =>
            new(new Discount { Code = code }, amount, amount is null ? DiscountReasons.MinSubtotal : null, shortfall);

        var ranked = DiscountRules.Rank([O("FAR", null, 50m), O("SMALL", 2m), O("NOTYET", null), O("NEAR", null, 5m), O("BIG", 8m)]);

        Assert.Equal(["BIG", "SMALL", "NEAR", "FAR", "NOTYET"], ranked.Select(o => o.Discount.Code));
    }

    [Fact]
    public async Task TheOffersOfACartListThePlatformCodesAndTheCodesOfItsShops()
    {
        var f = new Fixture();
        await f.MakeAsync(code: "ALL10");
        await f.MakeAsync(vendorId: Shop, code: "MUGS", type: "fixed", value: 5);
        await f.MakeAsync(vendorId: OtherShop, code: "TEA");
        await f.MakeAsync(code: "BIGCART", minSubtotal: 100);
        await f.MakeAsync(code: "ONCE", perCustomer: 1);
        await f.MakeAsync(code: "OFF", enabled: false);
        var once = f.Store.Discounts.Single(d => d.Code == "ONCE");
        f.Store.Usages.Add((once.Id, 1, Buyer, 1m));

        var offers = await f.Create().GetOffersAsync(Buyer, Cart((Shop, 40m)), CancellationToken.None);

        // Another shop's code and a switched-off code are not offered; the rest are, usable ones first.
        Assert.Equal(["MUGS", "ALL10", "BIGCART", "ONCE"], offers.Select(o => o.Discount.Code));
        Assert.Equal([5m, 4m], offers.Take(2).Select(o => o.Amount!.Value));
        Assert.Equal((DiscountReasons.MinSubtotal, (decimal?)60m), (offers[2].Reason, offers[2].Shortfall));
        Assert.Equal(DiscountReasons.CustomerLimitReached, offers[3].Reason);
        // Another customer has not used ONCE yet.
        Assert.NotNull((await f.Create().GetOffersAsync(Buyer + 1, Cart((Shop, 40m)), CancellationToken.None)).Single(o => o.Discount.Code == "ONCE").Amount);
        Assert.Empty(await f.Create().GetOffersAsync(Buyer, Cart(), CancellationToken.None));
    }

    // ---- Service: redeeming ----

    [Fact]
    public async Task TheLastUseGoesToOneBuyerOnly()
    {
        var f = new Fixture();
        var discount = await f.MakeAsync(maxUses: 1);
        var applied = new AppliedDiscount(discount, 4m, new Dictionary<int, decimal> { [Shop] = 4m });

        var first = await f.Create().RedeemAsync(applied, Buyer, 1, CancellationToken.None);
        var second = await f.Create().RedeemAsync(applied, Buyer + 1, 2, CancellationToken.None);

        Assert.Equal((RedeemOutcome.Redeemed, RedeemOutcome.LimitReached), (first, second));
        Assert.Equal(1, discount.UsedCount);
        Assert.Single(f.Audit.Events, e => e == "discount.redeemed");
    }

    [Fact]
    public async Task ThePerCustomerLimitIsEnforcedWhenRedeeming()
    {
        var f = new Fixture();
        var discount = await f.MakeAsync(perCustomer: 1);
        var applied = new AppliedDiscount(discount, 4m, new Dictionary<int, decimal> { [Shop] = 4m });

        Assert.Equal(RedeemOutcome.Redeemed, await f.Create().RedeemAsync(applied, Buyer, 1, CancellationToken.None));
        Assert.Equal(RedeemOutcome.CustomerLimitReached, await f.Create().RedeemAsync(applied, Buyer, 2, CancellationToken.None));
        Assert.Equal(RedeemOutcome.Redeemed, await f.Create().RedeemAsync(applied, Buyer + 1, 3, CancellationToken.None));
    }

    [Fact]
    public async Task ADisabledDiscountCannotBeRedeemedAndAnOrderUsesOneCodeOnce()
    {
        var f = new Fixture();
        var discount = await f.MakeAsync();
        var applied = new AppliedDiscount(discount, 4m, new Dictionary<int, decimal> { [Shop] = 4m });

        Assert.Equal(RedeemOutcome.Redeemed, await f.Create().RedeemAsync(applied, Buyer, 1, CancellationToken.None));
        Assert.Equal(RedeemOutcome.Unavailable, await f.Create().RedeemAsync(applied, Buyer, 1, CancellationToken.None));
        Assert.Equal(1, discount.UsedCount);

        discount.Enabled = false;
        Assert.Equal(RedeemOutcome.Unavailable, await f.Create().RedeemAsync(applied, Buyer, 2, CancellationToken.None));
    }

    [Fact]
    public async Task ReleasingGivesTheUseBackOnce()
    {
        var f = new Fixture();
        var discount = await f.MakeAsync(maxUses: 1);
        var applied = new AppliedDiscount(discount, 4m, new Dictionary<int, decimal> { [Shop] = 4m });
        await f.Create().RedeemAsync(applied, Buyer, 7, CancellationToken.None);

        await f.Create().ReleaseAsync(7, CancellationToken.None);
        await f.Create().ReleaseAsync(7, CancellationToken.None);
        await f.Create().ReleaseAsync(99, CancellationToken.None);

        Assert.Equal(0, discount.UsedCount);
        Assert.Empty(f.Store.Usages);
        Assert.Single(f.Audit.Events, e => e == "discount.released");
        Assert.Equal(RedeemOutcome.Redeemed, await f.Create().RedeemAsync(applied, Buyer + 1, 8, CancellationToken.None));
    }
}
