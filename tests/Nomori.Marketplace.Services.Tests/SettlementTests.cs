using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Settlement;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Authentication;
using Nomori.Marketplace.Services.Jobs;
using Nomori.Marketplace.Services.Settlement;

namespace Nomori.Marketplace.Services.Tests;

public sealed class SettlementTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);
    private const int Admin = 1;
    private const int Member = 10;
    private const int ShopA = 5;
    private const int ShopB = 6;
    private const string Password = "Correct-horse-1";

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class ReversibleProtector : ISecretProtector
    {
        public string Protect(string plain) => "enc:" + new string(plain.Reverse().ToArray());
        public string Unprotect(string protectedValue) => new string(protectedValue["enc:".Length..].Reverse().ToArray());
    }

    private sealed class FakeMembers : IVendorMemberStore
    {
        public Dictionary<int, List<VendorMember>> ByVendor { get; } = [];

        public Task<IReadOnlyList<VendorMember>> ListAsync(int vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VendorMember>>(ByVendor.GetValueOrDefault(vendorId) ?? []);

        public Task<VendorMember?> GetAsync(int vendorId, int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CreateMemberStoreResult> CreateAsync(int vendorId, string email, string? firstName, string? lastName, string passwordHash, string passwordSalt, string setupTokenHash, DateTime setupTokenExpiresOnUtc, int maxMembers, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReplaceSetupTokenAsync(int customerId, string tokenHash, DateTime expiresOnUtc, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoveMemberOutcome> RemoveAsync(int vendorId, int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
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

    /// <summary>The same rules as the SQL store, in memory: one sale per order, one refund per return, a payout claims each entry once.</summary>
    private sealed class FakeSettlementStore : ISettlementStore
    {
        private long nextEntry = 1;
        private int nextPayout = 1;

        public decimal DefaultRate { get; set; }
        public Dictionary<int, decimal> VendorRates { get; } = [];
        public List<SettlementOrderRow> CompletedOrders { get; } = [];
        public List<SettlementRefundRow> RefundedReturns { get; } = [];
        public List<SettlementOrderRow> Delivered { get; } = [];
        public List<SettlementEntry> Entries { get; } = [];
        public List<Payout> Payouts { get; } = [];
        public Dictionary<int, BankAccount> Banks { get; } = [];
        public Dictionary<int, string> Names { get; } = new() { [ShopA] = "Shop A", [ShopB] = "Shop B" };

        public Task<decimal> GetDefaultRateAsync(CancellationToken cancellationToken) => Task.FromResult(DefaultRate);
        public Task<decimal?> GetVendorRateAsync(int vendorId, CancellationToken cancellationToken) => Task.FromResult(VendorRates.TryGetValue(vendorId, out var r) ? (decimal?)r : null);

        public Task SetDefaultRateAsync(decimal rate, string? note, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            DefaultRate = rate;
            return Task.CompletedTask;
        }

        public Task SetVendorRateAsync(int vendorId, decimal? rate, string? note, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (rate is null) VendorRates.Remove(vendorId);
            else VendorRates[vendorId] = rate.Value;
            return Task.CompletedTask;
        }

        public Task<CommissionOverview> GetCommissionAsync(int historyTake, CancellationToken cancellationToken) =>
            Task.FromResult(new CommissionOverview(DefaultRate, [], []));

        public Task<IReadOnlyList<SettlementOrderRow>> FindUnrecordedSalesAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementOrderRow>>(
                CompletedOrders.Where(o => !Entries.Any(e => e.Kind == EntryKind.Sale && e.ShopOrderId == o.ShopOrderId)).Take(take).ToList());

        public Task<IReadOnlyList<SettlementRefundRow>> FindUnrecordedRefundsAsync(int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementRefundRow>>(
                RefundedReturns.Where(r => !Entries.Any(e => e.Kind == EntryKind.Refund && e.ReturnRequestId == r.ReturnRequestId)).Take(take).ToList());

        public Task<bool> TryInsertEntryAsync(SettlementEntry entry, CancellationToken cancellationToken)
        {
            var duplicate = Entries.Any(e => e.Kind == entry.Kind
                && (entry.Kind == EntryKind.Sale ? e.ShopOrderId == entry.ShopOrderId : entry.Kind == EntryKind.Refund && e.ReturnRequestId == entry.ReturnRequestId));
            if (duplicate) return Task.FromResult(false);
            entry.Id = nextEntry++;
            Entries.Add(entry);
            return Task.FromResult(true);
        }

        public Task<long> InsertAdjustmentAsync(SettlementEntry entry, CancellationToken cancellationToken)
        {
            entry.Id = nextEntry++;
            Entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task<VendorTotals> GetTotalsAsync(int vendorId, CancellationToken cancellationToken)
        {
            var mine = Entries.Where(e => e.VendorId == vendorId).ToList();
            decimal PayoutSum(PayoutStatus status) => mine.Where(e => e.PayoutId is { } id && Payouts.Single(p => p.Id == id).Status == status).Sum(e => e.Amount);
            return Task.FromResult(new VendorTotals(mine.Where(e => e.PayoutId is null).Sum(e => e.Amount), PayoutSum(PayoutStatus.Pending), PayoutSum(PayoutStatus.Paid)));
        }

        public Task<IReadOnlyList<SettlementOrderRow>> GetDeliveredAsync(int vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementOrderRow>>(Delivered.Where(o => o.VendorId == vendorId).ToList());

        public Task<PagedResult<SettlementEntry>> ListEntriesAsync(EntryQuery query, CancellationToken cancellationToken)
        {
            var all = Entries.Where(e => e.VendorId == query.VendorId && (query.Kind is null || e.Kind == query.Kind)).OrderByDescending(e => e.Id).ToList();
            return Task.FromResult(new PagedResult<SettlementEntry>(all.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToList(), all.Count, query.Page, query.PageSize));
        }

        public Task<IReadOnlyList<SettlementEntry>> ListPayoutEntriesAsync(int payoutId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SettlementEntry>>(Entries.Where(e => e.PayoutId == payoutId).ToList());

        public Task<IReadOnlyList<MonthlyTotal>> GetMonthlyAsync(int? vendorId, int months, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MonthlyTotal>>([]);

        public Task<PagedResult<VendorBalance>> ListBalancesAsync(BalanceQuery query, DateTime nowUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<VendorBalance>([], 0, query.Page, query.PageSize));

        public Task<BankAccount?> GetBankAccountAsync(int vendorId, CancellationToken cancellationToken) => Task.FromResult(Banks.GetValueOrDefault(vendorId));

        public Task SaveBankAccountAsync(BankAccount account, CancellationToken cancellationToken)
        {
            Banks[account.VendorId] = account;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<int>> VendorsWithEntriesAsync(DateTime cutoffUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(Entries.Where(e => e.PayoutId is null && e.CreatedOnUtc <= cutoffUtc).Select(e => e.VendorId).Distinct().Order().ToList());

        public Task<Payout?> TryCreatePayoutAsync(int vendorId, DateTime cutoffUtc, decimal minimum, BankAccount bank, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var claim = Entries.Where(e => e.VendorId == vendorId && e.PayoutId is null && e.CreatedOnUtc <= cutoffUtc).ToList();
            var sum = claim.Sum(e => e.Amount);
            if (claim.Count == 0 || sum <= 0m || sum < minimum) return Task.FromResult<Payout?>(null);

            var payout = new Payout
            {
                Id = nextPayout++, VendorId = vendorId, ShopName = Names[vendorId], Status = PayoutStatus.Pending, Amount = sum, CurrencyCode = "USD", EntryCount = claim.Count,
                CutoffUtc = cutoffUtc, BankName = bank.BankName, AccountNumberProtected = bank.AccountNumberProtected, AccountLast4 = bank.AccountLast4,
                HolderName = bank.HolderName, Branch = bank.Branch, CreatedByCustomerId = actorCustomerId, CreatedOnUtc = nowUtc, UpdatedOnUtc = nowUtc
            };
            payout.Number = SettlementRules.NumberFor(nowUtc, payout.Id);
            Payouts.Add(payout);
            foreach (var entry in claim) entry.PayoutId = payout.Id;
            return Task.FromResult<Payout?>(payout);
        }

        public Task<Payout?> GetPayoutAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Payouts.SingleOrDefault(p => p.Id == id));

        public Task<PagedResult<Payout>> ListPayoutsAsync(PayoutQuery query, CancellationToken cancellationToken)
        {
            var all = Payouts.Where(p => (query.VendorId is null || p.VendorId == query.VendorId) && (query.Status is null || p.Status == query.Status)).ToList();
            return Task.FromResult(new PagedResult<Payout>(all, all.Count, query.Page, query.PageSize));
        }

        public Task<IReadOnlyList<Payout>> ListPendingPayoutsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Payout>>(Payouts.Where(p => p.Status == PayoutStatus.Pending).ToList());

        public Task<bool> TryMarkPaidAsync(int id, string reference, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var payout = Payouts.Single(p => p.Id == id);
            if (payout.Status != PayoutStatus.Pending) return Task.FromResult(false);
            payout.Status = PayoutStatus.Paid;
            payout.Reference = reference;
            return Task.FromResult(true);
        }

        public Task<bool> TryMarkFailedAsync(int id, string reason, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var payout = Payouts.Single(p => p.Id == id);
            if (payout.Status != PayoutStatus.Pending) return Task.FromResult(false);
            payout.Status = PayoutStatus.Failed;
            payout.FailureReason = reason;
            foreach (var entry in Entries.Where(e => e.PayoutId == id)) entry.PayoutId = null;
            return Task.FromResult(true);
        }
    }

    private sealed class Fixture
    {
        public FakeSettlementStore Store { get; } = new();
        public FakeMembers Members { get; } = new();
        public RecordingQueue Queue { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public FakeVendorStore Vendors { get; } = new();
        public FakeCustomerIdentityStore Customers { get; } = new(new Customer { Id = Member, Email = "owner@shop.test", Active = true });
        public SettlementOptions Settings { get; } = new() { MinimumPayout = 10m, BankHoldHours = 48 };

        public Fixture()
        {
            Vendors.Vendors.Add(new Vendor { Id = ShopA, Name = "Shop A", Email = "a@shop.test", Active = true });
            Vendors.Vendors.Add(new Vendor { Id = ShopB, Name = "Shop B", Email = "b@shop.test", Active = true });
            Members.ByVendor[ShopA] = [new VendorMember { CustomerId = Member, Email = "owner@shop.test" }, new VendorMember { CustomerId = 11, Email = "second@shop.test" }];
            var (hash, salt) = new PasswordHasher().HashPassword(Password);
            Customers.Passwords[Member] = new CustomerPassword { CustomerId = Member, Password = hash, PasswordSalt = salt };
        }

        public SettlementService Create() => new(
            Store, Vendors, Members, Customers, new PasswordHasher(), new ReversibleProtector(), Queue, new FakePrimaryCurrency(), Audit, new FixedClock(),
            Options.Create(Settings), NullLogger<SettlementService>.Instance);

        public void GiveUsableBank(int vendorId) => Store.Banks[vendorId] = new BankAccount
        {
            VendorId = vendorId, BankName = "Vietcombank", AccountNumberProtected = new ReversibleProtector().Protect("123456789"), AccountLast4 = "6789",
            HolderName = "SHOP A", UpdatedOnUtc = Now.AddDays(-10), UsableFromUtc = Now.AddDays(-8)
        };

        public SettlementEntry AddSale(int vendorId, decimal amount, DateTime? on = null)
        {
            var entry = new SettlementEntry
            {
                Id = Store.Entries.Count + 1000, VendorId = vendorId, Kind = EntryKind.Sale, ShopOrderId = Store.Entries.Count + 1, Gross = amount, Amount = amount,
                CurrencyCode = "USD", CreatedOnUtc = on ?? Now.AddDays(-1)
            };
            Store.Entries.Add(entry);
            return entry;
        }
    }

    private static SettlementOrderRow Order(
        int id = 1, int vendor = ShopA, decimal subtotal = 100m, decimal shipping = 5m, decimal discount = 0m, string? funding = null, decimal tax = 0m, decimal rate = 10m,
        bool ghn = false, decimal shopShipping = 0m, decimal weightAdjustment = 0m) =>
        new(id, $"NM-{id}-1", vendor, subtotal, shipping, discount, funding, tax, rate, "USD", ghn, shopShipping, weightAdjustment);

    [Fact]
    public void WithGhnTheShopIsNotOwedTheShippingAndPaysForTheFreeShippingItOffered()
    {
        // Items 100 at 10%: 90 for the shop. The customer's shipping of a GHN order stays with the platform, which pays GHN.
        var paid = SettlementRules.Sale(Order(shipping: 5m, ghn: true), Now, 2);
        Assert.Equal((100m, 10m, 90m), (paid.Gross, paid.Commission, paid.Amount));
        Assert.Equal(5m, SettlementRules.PlatformShipping(Order(shipping: 5m, ghn: true)));

        // Free shipping: the customer paid none and the shop takes on GHN's 1.40.
        var free = SettlementRules.Sale(Order(shipping: 0m, ghn: true, shopShipping: 1.40m), Now, 2);
        Assert.Equal(88.60m, free.Amount);
        Assert.Equal(1.40m, SettlementRules.PlatformShipping(Order(shipping: 0m, ghn: true, shopShipping: 1.40m)));

        // GHN weighed the parcel heavier than the shop declared: the rise in fee comes off the shop's line and stays with the platform.
        var heavier = SettlementRules.Sale(Order(shipping: 5m, ghn: true, weightAdjustment: 0.60m), Now, 2);
        Assert.Equal(89.40m, heavier.Amount);
        Assert.Equal(5.60m, SettlementRules.PlatformShipping(Order(shipping: 5m, ghn: true, weightAdjustment: 0.60m)));

        // An order a shop shipped itself (before GHN) still pays the shipping to the shop.
        Assert.Equal(95m, SettlementRules.Sale(Order(shipping: 5m), Now, 2).Amount);
        Assert.Equal(0m, SettlementRules.PlatformShipping(Order(shipping: 5m)));
    }

    // ---- Rules ----

    [Fact]
    public void SaleKeepsCommissionOnTheItemsAndPaysTheShippingToTheShop()
    {
        var sale = SettlementRules.Sale(Order(), Now, 2);

        Assert.Equal(100m, sale.Gross);
        Assert.Equal(10m, sale.Commission);
        Assert.Equal(95m, sale.Amount);
    }

    [Fact]
    public void AShopFundedDiscountCostsTheShopButAPlatformFundedOneDoesNot()
    {
        var shopPays = SettlementRules.Sale(Order(discount: 20m, funding: "shop"), Now, 2);
        var platformPays = SettlementRules.Sale(Order(discount: 20m, funding: "platform"), Now, 2);

        Assert.Equal(80m, shopPays.Gross);
        Assert.Equal(8m, shopPays.Commission);
        Assert.Equal(77m, shopPays.Amount);
        Assert.Equal(95m, platformPays.Amount);
    }

    [Fact]
    public void CommissionRoundsHalfAwayFromZeroToTheCurrencyPlaces()
    {
        Assert.Equal(2.50m, SettlementRules.Commission(33.33m, 7.5m, 2));
        Assert.Equal(3m, SettlementRules.Commission(1000m, 0.3m, 0));
    }

    [Fact]
    public void ARefundTakesBackTheMatchingShareOfTheRevenueAndNeverTheShipping()
    {
        // The customer paid 100 - 10 discount + 6 tax = 96 for the items and gets 48 back: half.
        var refund = SettlementRules.Refund(new SettlementRefundRow(7, 48m, Order(discount: 10m, funding: "platform", tax: 6m)), Now, 2);

        Assert.Equal(EntryKind.Refund, refund.Kind);
        Assert.Equal(-50m, refund.Gross);
        Assert.Equal(5m, refund.Commission);
        Assert.Equal(-45m, refund.Amount);
    }

    [Fact]
    public void ASaleAndItsFullRefundCancelOutExceptTheShipping()
    {
        var order = Order(tax: 0m);
        var sale = SettlementRules.Sale(order, Now, 2);
        var refund = SettlementRules.Refund(new SettlementRefundRow(1, 100m, order), Now, 2);

        Assert.Equal(order.ShippingFee, sale.Amount + refund.Amount);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(50, true)]
    [InlineData(50.01, false)]
    [InlineData(-1, false)]
    [InlineData(12.345, false)]
    public void RatesAreBetweenZeroAndFiftyWithTwoDecimals(double rate, bool valid) =>
        Assert.Equal(valid, SettlementRules.ValidRate((decimal)rate));

    [Fact]
    public void ACsvCellIsQuotedAndAFormulaIsNeutralised()
    {
        Assert.Equal("\"a \"\"b\"\"\"", SettlementRules.Csv("a \"b\""));
        Assert.Equal("\"'=SUM(A1)\"", SettlementRules.Csv("=SUM(A1)"));
        Assert.Equal("\"\"", SettlementRules.Csv(null));
    }

    [Fact]
    public void ABankAccountIsUsableOnlyAfterItsHold()
    {
        var account = new BankAccount { UsableFromUtc = Now.AddHours(1) };

        Assert.Equal("missing", SettlementRules.BankStatus(null, Now));
        Assert.Equal("hold", SettlementRules.BankStatus(account, Now));
        Assert.Equal("ready", SettlementRules.BankStatus(account, Now.AddHours(2)));
    }

    // ---- Recording ----

    [Fact]
    public async Task ACompletedOrderGivesOneSaleEvenWhenTheJobRunsTwice()
    {
        var f = new Fixture();
        f.Store.CompletedOrders.Add(Order());
        var service = f.Create();

        var first = await service.RecordPendingAsync(CancellationToken.None);
        var second = await service.RecordPendingAsync(CancellationToken.None);

        Assert.Equal((1, 0), first);
        Assert.Equal((0, 0), second);
        Assert.Single(f.Store.Entries);
        Assert.Equal(95m, f.Store.Entries[0].Amount);
    }

    [Fact]
    public async Task ARefundedReturnGivesOneNegativeEntry()
    {
        var f = new Fixture();
        f.Store.RefundedReturns.Add(new SettlementRefundRow(3, 100m, Order()));
        var service = f.Create();

        await service.RecordPendingAsync(CancellationToken.None);
        await service.RecordPendingAsync(CancellationToken.None);

        var entry = Assert.Single(f.Store.Entries);
        Assert.Equal(EntryKind.Refund, entry.Kind);
        Assert.True(entry.Amount < 0m);
    }

    [Fact]
    public async Task TheRecordingJobReportsWhatItWrote()
    {
        var f = new Fixture();
        f.Store.CompletedOrders.Add(Order(1));
        f.Store.CompletedOrders.Add(Order(2));

        var result = await new RecordSettlementEntriesJob(f.Create()).RunAsync(CancellationToken.None);

        Assert.Equal(2, result.Processed);
        Assert.Equal(0, result.Failed);
    }

    // ---- Commission ----

    [Fact]
    public async Task TheDefaultRateIsValidatedAndAudited()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.Contains("rate", (await service.SetDefaultRateAsync(60m, null, Admin, CancellationToken.None)).Errors.Keys);
        var ok = await service.SetDefaultRateAsync(12.5m, "launch", Admin, CancellationToken.None);

        Assert.True(ok.Succeeded);
        Assert.Equal(12.5m, f.Store.DefaultRate);
        Assert.Contains("finance.rate_changed", f.Audit.Events);
    }

    [Fact]
    public async Task ChangingAShopRateEmailsEveryMemberAndAnUnknownShopIsNotFound()
    {
        var f = new Fixture();
        f.Store.DefaultRate = 10m;
        var service = f.Create();

        var result = await service.SetVendorRateAsync(ShopA, 5m, "deal", Admin, CancellationToken.None);
        var missing = await service.SetVendorRateAsync(99, 5m, null, Admin, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(5m, f.Store.VendorRates[ShopA]);
        Assert.Equal(2, f.Queue.Queued.Count);
        Assert.All(f.Queue.Queued, q => Assert.Equal(SettlementEmailKinds.RateChanged, q.Kind));
        Assert.Contains("10%", f.Queue.Queued[0].Message.HtmlBody);
        Assert.Contains("5%", f.Queue.Queued[0].Message.HtmlBody);
        Assert.Equal(CatalogErrors.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task RemovingAShopRateFallsBackToTheDefault()
    {
        var f = new Fixture();
        f.Store.DefaultRate = 10m;
        f.Store.VendorRates[ShopA] = 3m;

        await f.Create().SetVendorRateAsync(ShopA, null, null, Admin, CancellationToken.None);

        Assert.DoesNotContain(ShopA, f.Store.VendorRates.Keys);
        Assert.Equal(10m, (await f.Create().GetOverviewAsync(ShopA, CancellationToken.None)).Rate);
    }

    // ---- Adjustments ----

    [Fact]
    public async Task AnAdjustmentNeedsAReasonAndANonZeroAmountWithinTheCurrencyScale()
    {
        var f = new Fixture();
        var service = f.Create();

        var noReason = await service.AdjustAsync(new NewAdjustment(ShopA, 5m, " "), Admin, CancellationToken.None);
        var zero = await service.AdjustAsync(new NewAdjustment(ShopA, 0m, "x"), Admin, CancellationToken.None);
        var scale = await service.AdjustAsync(new NewAdjustment(ShopA, 1.234m, "x"), Admin, CancellationToken.None);
        var ok = await service.AdjustAsync(new NewAdjustment(ShopA, -5m, "chargeback"), Admin, CancellationToken.None);
        var missing = await service.AdjustAsync(new NewAdjustment(99, 5m, "x"), Admin, CancellationToken.None);

        Assert.Contains("reason", noReason.Errors.Keys);
        Assert.Contains("amount", zero.Errors.Keys);
        Assert.Contains("amount", scale.Errors.Keys);
        Assert.True(ok.Succeeded);
        Assert.Equal(-5m, ok.Value!.Amount);
        Assert.Equal(EntryKind.Adjustment, ok.Value.Kind);
        Assert.Equal(CatalogErrors.NotFound, missing.ErrorCode);
    }

    // ---- Bank account ----

    [Fact]
    public async Task ChangingTheBankAccountNeedsTheRightPassword()
    {
        var f = new Fixture();

        var result = await f.Create().SaveBankAccountAsync(
            ShopA, new SaveBankAccountCommand("Vietcombank", "123456789", "SHOP A", null, "wrong"), Member, CancellationToken.None);

        Assert.Contains("password", result.Errors.Keys);
        Assert.Empty(f.Store.Banks);
        Assert.Empty(f.Queue.Queued);
    }

    [Fact]
    public async Task ABankAccountIsStoredEncryptedShownMaskedAndEmailedWithoutTheNumber()
    {
        var f = new Fixture();

        var result = await f.Create().SaveBankAccountAsync(
            ShopA, new SaveBankAccountCommand("vietcombank", "1234 5678 9", "SHOP A", "Hanoi", Password), Member, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("**** 6789", result.Value!.MaskedNumber);
        Assert.False(result.Value.Usable);
        var stored = f.Store.Banks[ShopA];
        Assert.DoesNotContain("123456789", stored.AccountNumberProtected);
        Assert.Equal(Now.AddHours(48), stored.UsableFromUtc);
        Assert.Equal(2, f.Queue.Queued.Count);
        Assert.All(f.Queue.Queued, q =>
        {
            Assert.Equal(SettlementEmailKinds.BankChanged, q.Kind);
            Assert.Contains("6789", q.Message.HtmlBody);
            Assert.DoesNotContain("123456789", q.Message.HtmlBody);
        });
        Assert.DoesNotContain("123456789", System.Text.Json.JsonSerializer.Serialize(f.Audit.Entries.Select(e => e.Details)));
    }

    [Theory]
    [InlineData("Unknown Bank", "123456789", "bankName")]
    [InlineData("ACB", "12ab56", "accountNumber")]
    [InlineData("ACB", "123", "accountNumber")]
    public async Task ABankAccountIsValidated(string bank, string number, string field)
    {
        var f = new Fixture();

        var result = await f.Create().SaveBankAccountAsync(ShopA, new SaveBankAccountCommand(bank, number, "SHOP A", null, Password), Member, CancellationToken.None);

        Assert.Contains(field, result.Errors.Keys);
    }

    // ---- Payouts ----

    [Fact]
    public async Task AShopWithoutAUsableBankAccountOrBelowTheMinimumIsSkipped()
    {
        var f = new Fixture();
        f.AddSale(ShopA, 100m);                 // no bank account
        f.AddSale(ShopB, 5m);                   // under the minimum
        f.GiveUsableBank(ShopB);
        var service = f.Create();

        var run = await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None);

        Assert.Empty(run.Value!.Created);
        Assert.Equal(2, run.Value.Skipped);

        f.Store.Banks[ShopA] = new BankAccount { VendorId = ShopA, BankName = "ACB", AccountNumberProtected = "enc:x", AccountLast4 = "1234", HolderName = "A", UsableFromUtc = Now.AddHours(5) };
        Assert.Empty((await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None)).Value!.Created);
    }

    [Fact]
    public async Task APayoutTakesEveryEntryUpToTheCutoffOnceAndLeavesLaterOnesAlone()
    {
        var f = new Fixture();
        f.GiveUsableBank(ShopA);
        f.AddSale(ShopA, 60m, Now.AddDays(-3));
        f.AddSale(ShopA, 50m, Now.AddDays(-2));
        var later = f.AddSale(ShopA, 40m, Now.AddHours(-1));
        var service = f.Create();

        var run = await service.CreatePayoutsAsync(Now.AddDays(-1), Admin, CancellationToken.None);
        var again = await service.CreatePayoutsAsync(Now.AddDays(-1), Admin, CancellationToken.None);

        var payout = Assert.Single(run.Value!.Created);
        Assert.Equal(110m, payout.Amount);
        Assert.Equal(2, payout.EntryCount);
        Assert.Equal("6789", payout.AccountLast4);
        Assert.Null(later.PayoutId);
        Assert.Empty(again.Value!.Created);
        Assert.Contains("finance.payout_created", f.Audit.Events);
    }

    [Fact]
    public async Task ACutoffInTheFutureIsRefused()
    {
        var f = new Fixture();

        var result = await f.Create().CreatePayoutsAsync(Now.AddDays(1), Admin, CancellationToken.None);

        Assert.Contains("cutoff", result.Errors.Keys);
    }

    [Fact]
    public async Task ANegativeBalanceIsNotPaidAndIsCarriedForward()
    {
        var f = new Fixture();
        f.GiveUsableBank(ShopA);
        f.AddSale(ShopA, 100m, Now.AddDays(-3));
        f.AddSale(ShopA, -150m, Now.AddDays(-2));

        var run = await f.Create().CreatePayoutsAsync(Now, Admin, CancellationToken.None);

        Assert.Empty(run.Value!.Created);
        Assert.All(f.Store.Entries, e => Assert.Null(e.PayoutId));
    }

    [Fact]
    public async Task APayoutIsPaidOnceAndTheMembersAreTold()
    {
        var f = new Fixture();
        f.GiveUsableBank(ShopA);
        f.AddSale(ShopA, 100m);
        var service = f.Create();
        var payout = (await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None)).Value!.Created.Single();

        var noReference = await service.MarkPaidAsync(payout.Id, " ", Admin, CancellationToken.None);
        var paid = await service.MarkPaidAsync(payout.Id, "FT123", Admin, CancellationToken.None);
        var twice = await service.MarkPaidAsync(payout.Id, "FT124", Admin, CancellationToken.None);
        var thenFailed = await service.MarkFailedAsync(payout.Id, "wrong account", Admin, CancellationToken.None);

        Assert.Contains("reference", noReference.Errors.Keys);
        Assert.Equal(PayoutStatus.Paid, paid.Value!.Status);
        Assert.Equal("FT123", paid.Value.Reference);
        Assert.Equal(SettlementErrors.PayoutInvalidTransition, twice.ErrorCode);
        Assert.Equal(SettlementErrors.PayoutInvalidTransition, thenFailed.ErrorCode);
        Assert.Equal(2, f.Queue.Queued.Count(q => q.Kind == SettlementEmailKinds.PayoutPaid));

        var overview = await service.GetOverviewAsync(ShopA, CancellationToken.None);
        Assert.Equal(100m, overview.Paid);
        Assert.Equal(0m, overview.Available);
    }

    [Fact]
    public async Task AFailedPayoutGivesTheMoneyBackToTheBalanceForTheNextRun()
    {
        var f = new Fixture();
        f.GiveUsableBank(ShopA);
        f.AddSale(ShopA, 100m);
        var service = f.Create();
        var payout = (await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None)).Value!.Created.Single();

        var reasonless = await service.MarkFailedAsync(payout.Id, "", Admin, CancellationToken.None);
        var failed = await service.MarkFailedAsync(payout.Id, "wrong account number", Admin, CancellationToken.None);
        var next = await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None);

        Assert.Contains("reason", reasonless.Errors.Keys);
        Assert.Equal(PayoutStatus.Failed, failed.Value!.Status);
        Assert.Contains("check your bank account", f.Queue.Queued.Last().Message.HtmlBody);
        Assert.Single(next.Value!.Created);
        Assert.NotEqual(payout.Id, next.Value.Created[0].Id);
    }

    [Fact]
    public async Task AnUnknownPayoutIsNotFound()
    {
        var f = new Fixture();
        var service = f.Create();

        Assert.Equal(CatalogErrors.NotFound, (await service.MarkPaidAsync(99, "x", Admin, CancellationToken.None)).ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, (await service.MarkFailedAsync(99, "x", Admin, CancellationToken.None)).ErrorCode);
    }

    // ---- Visibility ----

    [Fact]
    public async Task AShopSeesOnlyItsOwnPayouts()
    {
        var f = new Fixture();
        f.GiveUsableBank(ShopA);
        f.GiveUsableBank(ShopB);
        f.AddSale(ShopA, 100m);
        f.AddSale(ShopB, 100m);
        var service = f.Create();
        await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None);
        var other = f.Store.Payouts.Single(p => p.VendorId == ShopB);

        var mine = await service.GetPayoutsAsync(new SettlementCaller(false, Member, ShopA), null, 1, 20, CancellationToken.None);
        var all = await service.GetPayoutsAsync(new SettlementCaller(true, Admin, null), null, 1, 20, CancellationToken.None);
        var foreign = await service.GetPayoutAsync(new SettlementCaller(false, Member, ShopA), other.Id, CancellationToken.None);
        var foreignCsv = await service.ExportPayoutCsvAsync(ShopA, other.Id, CancellationToken.None);

        Assert.Equal(ShopA, Assert.Single(mine.Items).VendorId);
        Assert.Equal(2, all.TotalCount);
        Assert.Equal(CatalogErrors.NotFound, foreign.ErrorCode);
        Assert.Equal(CatalogErrors.NotFound, foreignCsv.ErrorCode);
    }

    [Fact]
    public async Task ThePendingBalanceCountsDeliveredOrdersAndTheAvailableOneOnlyTheLedger()
    {
        var f = new Fixture();
        f.Store.Delivered.Add(Order(rate: 10m));
        f.AddSale(ShopA, 40m);

        var overview = await f.Create().GetOverviewAsync(ShopA, CancellationToken.None);

        Assert.Equal(95m, overview.Pending);
        Assert.Equal(40m, overview.Available);
        Assert.Equal("missing", overview.BankStatus);
    }

    // ---- CSV ----

    [Fact]
    public async Task OnlyTheAdministratorSheetCarriesTheFullAccountNumber()
    {
        var f = new Fixture();
        f.GiveUsableBank(ShopA);
        f.AddSale(ShopA, 100m);
        var service = f.Create();
        var payout = (await service.CreatePayoutsAsync(Now, Admin, CancellationToken.None)).Value!.Created.Single();

        var admin = await service.ExportPendingPayoutsCsvAsync(Admin, CancellationToken.None);
        var shop = await service.ExportPayoutCsvAsync(ShopA, payout.Id, CancellationToken.None);

        Assert.Contains("\"123456789\"", admin);
        Assert.Contains("\"Shop A\"", admin);
        Assert.DoesNotContain("123456789", shop.Value);
        Assert.Contains("finance.payout_exported", f.Audit.Events);
    }
}
