using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Core.Settlement;

/// <summary>Settings of settlement (section <c>Settlement</c>).</summary>
public sealed class SettlementOptions
{
    public const string SectionName = "Settlement";

    /// <summary>A shop is paid only when its available balance is at least this (in the primary currency).</summary>
    public decimal MinimumPayout { get; set; } = 10m;

    /// <summary>How long a new or changed bank account waits before a payout may use it.</summary>
    public int BankHoldHours { get; set; } = 48;
}

public static class SettlementLimits
{
    public const decimal MaxRate = 50m;
    public const int MaxNoteLength = 500;
    public const int MaxReferenceLength = 100;
    public const int MaxReasonLength = 500;
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxPageSize = 100;
    public const int BatchSize = 200;
    public const int MinAccountDigits = 6;
    public const int MaxAccountDigits = 30;
}

/// <summary>Business-rule codes of settlement (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class SettlementErrors
{
    public const string NoBankAccount = "finance.no_bank_account";
    public const string PayoutInvalidTransition = "finance.payout_invalid_transition";
}

/// <summary>The stored value is part of the database contract: add at the end, never renumber.</summary>
public enum EntryKind
{
    Sale = 0,
    Refund = 1,
    Adjustment = 2
}

public enum PayoutStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2
}

public static class SettlementEmailKinds
{
    public const string RateChanged = "finance.rate_changed";
    public const string BankChanged = "finance.bank_changed";
    public const string PayoutPaid = "finance.payout_paid";
    public const string PayoutFailed = "finance.payout_failed";
}

/// <summary>One line of a shop's money. Immutable: only <see cref="PayoutId"/> moves, when the line is put in a payout (or taken out of a failed one).</summary>
public sealed class SettlementEntry
{
    public long Id { get; set; }
    public int VendorId { get; set; }
    public EntryKind Kind { get; set; }
    public int? ShopOrderId { get; set; }
    public int? ReturnRequestId { get; set; }
    public string? ShopOrderNumber { get; set; }

    /// <summary>The goods this line is about (after a shop-funded discount); negative for a refund.</summary>
    public decimal Gross { get; set; }

    /// <summary>The commission kept by the platform (a sale), or given back (a refund, positive).</summary>
    public decimal Commission { get; set; }

    /// <summary>What it does to the shop's balance. Negative takes money away.</summary>
    public decimal Amount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int? PayoutId { get; set; }
    public int? CreatedByCustomerId { get; set; }
    public DateTime CreatedOnUtc { get; set; }
}

public sealed class BankAccount
{
    public int VendorId { get; set; }
    public string BankName { get; set; } = string.Empty;

    /// <summary>Encrypted. Never leaves the service except in a payout sheet for an administrator.</summary>
    public string AccountNumberProtected { get; set; } = string.Empty;

    public string AccountLast4 { get; set; } = string.Empty;
    public string HolderName { get; set; } = string.Empty;
    public string? Branch { get; set; }
    public int? UpdatedByCustomerId { get; set; }
    public DateTime UpdatedOnUtc { get; set; }

    /// <summary>A payout may use the account from this moment on.</summary>
    public DateTime UsableFromUtc { get; set; }
}

public sealed class Payout
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public string ShopName { get; set; } = string.Empty;
    public PayoutStatus Status { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = string.Empty;
    public int EntryCount { get; set; }
    public DateTime CutoffUtc { get; set; }

    // The bank account as it was when the payout was made.
    public string BankName { get; set; } = string.Empty;
    public string AccountNumberProtected { get; set; } = string.Empty;
    public string AccountLast4 { get; set; } = string.Empty;
    public string HolderName { get; set; } = string.Empty;
    public string? Branch { get; set; }

    public string? Reference { get; set; }
    public string? FailureReason { get; set; }
    public int CreatedByCustomerId { get; set; }
    public int? ResolvedByCustomerId { get; set; }
    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public sealed record CommissionChange(
    int Id, int? VendorId, string? ShopName, decimal? OldRate, decimal? NewRate, string? Note, int ChangedByCustomerId, DateTime ChangedOnUtc);

public sealed record VendorCommission(int VendorId, string ShopName, decimal Rate, string? Note, DateTime UpdatedOnUtc);

public sealed record CommissionOverview(decimal DefaultRate, IReadOnlyList<VendorCommission> Overrides, IReadOnlyList<CommissionChange> History);

/// <summary>A shop order as settlement sees it. <see cref="Rate"/> is the one frozen when the order was created.</summary>
public sealed record SettlementOrderRow(
    int ShopOrderId, string Number, int VendorId, decimal Subtotal, decimal ShippingFee, decimal DiscountAmount, string? DiscountFunding,
    decimal TaxAmount, decimal Rate, string CurrencyCode);

/// <summary>A refunded return and the order it belongs to.</summary>
public sealed record SettlementRefundRow(int ReturnRequestId, decimal RefundAmount, SettlementOrderRow Order);

public sealed record EntryQuery(int VendorId, EntryKind? Kind, DateTime? FromUtc, DateTime? ToUtc, string? Search, int Page, int PageSize);

public sealed record PayoutQuery(int? VendorId, PayoutStatus? Status, int Page, int PageSize);

public sealed record VendorTotals(decimal Available, decimal InPayout, decimal Paid);

public sealed record MonthlyTotal(string Month, decimal Sales, decimal Commission, decimal Refunds);

public sealed record BalanceQuery(string? Bank, string? Search, int Page, int PageSize);

/// <summary>Bank status of a shop for the administrator: <c>missing</c>, <c>hold</c> or <c>ready</c>.</summary>
public sealed record VendorBalance(
    int VendorId, string ShopName, decimal Available, decimal InPayout, decimal Paid, string BankStatus, string? AccountLast4, string CurrencyCode);

public sealed record FinanceOverview(
    decimal Rate, decimal Pending, decimal Available, decimal InPayout, decimal Paid, string CurrencyCode, decimal MinimumPayout,
    string BankStatus, IReadOnlyList<MonthlyTotal> Months);

/// <summary>What a bank account looks like to the shop: never the full number.</summary>
public sealed record BankAccountView(
    string BankName, string MaskedNumber, string HolderName, string? Branch, DateTime UpdatedOnUtc, DateTime UsableFromUtc, bool Usable);

public sealed record SaveBankAccountCommand(string? BankName, string? AccountNumber, string? HolderName, string? Branch, string? Password);

public sealed record PayoutRunResult(IReadOnlyList<Payout> Created, int Skipped);

public sealed record PayoutDetail(Payout Payout, IReadOnlyList<SettlementEntry> Entries);

/// <summary>A payout sheet for an administrator: the full account number is here, because the transfer needs it.</summary>
public sealed record PayoutSheetRow(Payout Payout, string AccountNumber);

public sealed record NewAdjustment(int VendorId, decimal Amount, string? Reason);

public sealed record SettlementCaller(bool IsAdmin, int CustomerId, int? VendorId);

public static class SettlementBanks
{
    public static readonly IReadOnlyList<string> All =
    [
        "Vietcombank", "VietinBank", "BIDV", "Agribank", "Techcombank", "MB Bank", "ACB", "VPBank", "TPBank", "Sacombank",
        "HDBank", "SHB", "VIB", "MSB", "OCB", "SeABank", "LPBank", "Eximbank", "Nam A Bank", "Other"
    ];
}

// ---- Rules ----

public static class SettlementRules
{
    public static bool ValidRate(decimal rate) => rate >= 0m && rate <= SettlementLimits.MaxRate && decimal.Round(rate, 2) == rate;

    public static decimal Round(decimal value, int places) => Math.Round(value, places, MidpointRounding.AwayFromZero);

    /// <summary>What commission is charged on: the items, less a discount the shop pays for (a platform-funded discount costs the shop nothing).</summary>
    public static decimal Base(SettlementOrderRow order) =>
        order.Subtotal - (order.DiscountFunding == "shop" ? order.DiscountAmount : 0m);

    public static decimal Commission(decimal baseAmount, decimal rate, int places) => Round(baseAmount * rate / 100m, places);

    /// <summary>The sale line of a completed shop order. Shipping is added after the commission: the shop owes it to the carrier.</summary>
    public static SettlementEntry Sale(SettlementOrderRow order, DateTime nowUtc, int places)
    {
        var goods = Base(order);
        var commission = Commission(goods, order.Rate, places);
        return new SettlementEntry
        {
            VendorId = order.VendorId, Kind = EntryKind.Sale, ShopOrderId = order.ShopOrderId, ShopOrderNumber = order.Number,
            Gross = goods, Commission = commission, Amount = goods - commission + order.ShippingFee, CurrencyCode = order.CurrencyCode, CreatedOnUtc = nowUtc
        };
    }

    /// <summary>
    /// The refund line of a return: the shop gives back the share of its revenue that matches the share of the items the customer got back.
    /// Shipping is not refunded. The commission on that share is returned to the shop.
    /// </summary>
    public static SettlementEntry Refund(SettlementRefundRow refund, DateTime nowUtc, int places)
    {
        var order = refund.Order;
        var paidForItems = order.Subtotal - order.DiscountAmount + order.TaxAmount;
        var ratio = paidForItems <= 0m ? 0m : Math.Clamp(refund.RefundAmount / paidForItems, 0m, 1m);
        var goods = Base(order);
        var commission = Commission(goods, order.Rate, places);
        var gross = Round(ratio * goods, places);
        var returnedCommission = Round(ratio * commission, places);
        return new SettlementEntry
        {
            VendorId = order.VendorId, Kind = EntryKind.Refund, ShopOrderId = order.ShopOrderId, ReturnRequestId = refund.ReturnRequestId,
            ShopOrderNumber = order.Number, Gross = -gross, Commission = returnedCommission, Amount = -(gross - returnedCommission),
            CurrencyCode = order.CurrencyCode, CreatedOnUtc = nowUtc
        };
    }

    public static string Last4(string accountNumber) =>
        accountNumber.Length <= 4 ? accountNumber : accountNumber[^4..];

    public static string Masked(string last4) => $"**** {last4}";

    public static bool IsUsable(BankAccount? account, DateTime nowUtc) => account is not null && account.UsableFromUtc <= nowUtc;

    public static string BankStatus(BankAccount? account, DateTime nowUtc) =>
        account is null ? "missing" : account.UsableFromUtc <= nowUtc ? "ready" : "hold";

    public static string NumberFor(DateTime createdOnUtc, int id) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PO{createdOnUtc:yyMMdd}-{id:D4}");

    public static string ToWire(EntryKind kind) => kind switch
    {
        EntryKind.Sale => "sale",
        EntryKind.Refund => "refund",
        _ => "adjustment"
    };

    public static bool TryParse(string? value, out EntryKind kind)
    {
        foreach (var candidate in Enum.GetValues<EntryKind>())
        {
            if (string.Equals(ToWire(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }
        kind = default;
        return false;
    }

    public static string ToWire(PayoutStatus status) => status switch
    {
        PayoutStatus.Pending => "pending",
        PayoutStatus.Paid => "paid",
        _ => "failed"
    };

    public static bool TryParse(string? value, out PayoutStatus status)
    {
        foreach (var candidate in Enum.GetValues<PayoutStatus>())
        {
            if (string.Equals(ToWire(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }
        status = default;
        return false;
    }

    /// <summary>A cell of a CSV file: quoted, and a leading formula character neutralised so a spreadsheet does not run it.</summary>
    public static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}

// ---- Store and service ----

public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

public interface ISettlementStore
{
    // Commission
    Task<decimal> GetDefaultRateAsync(CancellationToken cancellationToken);
    Task<decimal?> GetVendorRateAsync(int vendorId, CancellationToken cancellationToken);
    Task SetDefaultRateAsync(decimal rate, string? note, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Sets the shop's own rate; null removes it. Writes the history row in the same transaction.</summary>
    Task SetVendorRateAsync(int vendorId, decimal? rate, string? note, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    Task<CommissionOverview> GetCommissionAsync(int historyTake, CancellationToken cancellationToken);

    // Recording (D6)
    Task<IReadOnlyList<SettlementOrderRow>> FindUnrecordedSalesAsync(int take, CancellationToken cancellationToken);
    Task<IReadOnlyList<SettlementRefundRow>> FindUnrecordedRefundsAsync(int take, CancellationToken cancellationToken);

    /// <summary>Writes the entry; false when the unique index says that shop order (sale) or return (refund) is already recorded.</summary>
    Task<bool> TryInsertEntryAsync(SettlementEntry entry, CancellationToken cancellationToken);

    Task<long> InsertAdjustmentAsync(SettlementEntry entry, CancellationToken cancellationToken);

    // Reads
    Task<VendorTotals> GetTotalsAsync(int vendorId, CancellationToken cancellationToken);

    /// <summary>Shop orders that were delivered and not yet confirmed received: their revenue is "pending".</summary>
    Task<IReadOnlyList<SettlementOrderRow>> GetDeliveredAsync(int vendorId, CancellationToken cancellationToken);

    Task<PagedResult<SettlementEntry>> ListEntriesAsync(EntryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<SettlementEntry>> ListPayoutEntriesAsync(int payoutId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonthlyTotal>> GetMonthlyAsync(int? vendorId, int months, CancellationToken cancellationToken);
    Task<PagedResult<VendorBalance>> ListBalancesAsync(BalanceQuery query, DateTime nowUtc, CancellationToken cancellationToken);

    // Bank account
    Task<BankAccount?> GetBankAccountAsync(int vendorId, CancellationToken cancellationToken);
    Task SaveBankAccountAsync(BankAccount account, CancellationToken cancellationToken);

    // Payouts
    /// <summary>
    /// For one shop, in one transaction: claims the unclaimed entries up to the cut-off with one UPDATE, adds them up and writes the payout.
    /// When the sum is below the minimum (or not positive) nothing is written. Null in that case.
    /// </summary>
    Task<Payout?> TryCreatePayoutAsync(int vendorId, DateTime cutoffUtc, decimal minimum, BankAccount bank, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Shops that have unclaimed entries up to the cut-off.</summary>
    Task<IReadOnlyList<int>> VendorsWithEntriesAsync(DateTime cutoffUtc, CancellationToken cancellationToken);

    Task<Payout?> GetPayoutAsync(int id, CancellationToken cancellationToken);
    Task<PagedResult<Payout>> ListPayoutsAsync(PayoutQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<Payout>> ListPendingPayoutsAsync(CancellationToken cancellationToken);
    Task<bool> TryMarkPaidAsync(int id, string reference, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Pending to failed, and the payout's entries go back to the balance, in one transaction.</summary>
    Task<bool> TryMarkFailedAsync(int id, string reason, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface ISettlementService
{
    // ---- Recording (the job) ----
    Task<(int Recorded, int Failed)> RecordPendingAsync(CancellationToken cancellationToken);

    // ---- Administrators ----
    Task<CommissionOverview> GetCommissionAsync(CancellationToken cancellationToken);
    Task<CatalogResult<decimal>> SetDefaultRateAsync(decimal rate, string? note, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<decimal?>> SetVendorRateAsync(int vendorId, decimal? rate, string? note, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<SettlementEntry>> AdjustAsync(NewAdjustment command, int actorCustomerId, CancellationToken cancellationToken);
    Task<PagedResult<VendorBalance>> GetBalancesAsync(BalanceQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MonthlyTotal>> GetPlatformMonthlyAsync(int months, CancellationToken cancellationToken);
    Task<CatalogResult<PayoutRunResult>> CreatePayoutsAsync(DateTime cutoffUtc, int actorCustomerId, CancellationToken cancellationToken);
    Task<PagedResult<Payout>> GetPayoutsAsync(SettlementCaller caller, PayoutStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<CatalogResult<PayoutDetail>> GetPayoutAsync(SettlementCaller caller, int id, CancellationToken cancellationToken);
    Task<CatalogResult<Payout>> MarkPaidAsync(int id, string? reference, int actorCustomerId, CancellationToken cancellationToken);
    Task<CatalogResult<Payout>> MarkFailedAsync(int id, string? reason, int actorCustomerId, CancellationToken cancellationToken);

    /// <summary>The CSV of every pending payout, with full account numbers, for the transfer. Audited.</summary>
    Task<string> ExportPendingPayoutsCsvAsync(int actorCustomerId, CancellationToken cancellationToken);

    // ---- Shop members (the caller has been checked against the shop) ----
    Task<FinanceOverview> GetOverviewAsync(int vendorId, CancellationToken cancellationToken);
    Task<PagedResult<SettlementEntry>> GetEntriesAsync(EntryQuery query, CancellationToken cancellationToken);
    Task<CatalogResult<string>> ExportPayoutCsvAsync(int vendorId, int payoutId, CancellationToken cancellationToken);
    Task<BankAccountView?> GetBankAccountAsync(int vendorId, CancellationToken cancellationToken);
    Task<CatalogResult<BankAccountView>> SaveBankAccountAsync(int vendorId, SaveBankAccountCommand command, int actorCustomerId, CancellationToken cancellationToken);
}
