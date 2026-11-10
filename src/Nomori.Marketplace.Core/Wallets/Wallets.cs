using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Wallets;

/// <summary>
/// The customer's wallet: money topped up through a gateway, spent at checkout, and where refunds of returns go. The balance never goes
/// below zero; every change is a ledger line that is never edited, and the balance can always be added up from them.
/// </summary>
public sealed record Wallet(int CustomerId, decimal Balance, string CurrencyCode, DateTime? UpdatedOnUtc);

/// <summary>The stored value is part of the database contract: add at the end, never renumber.</summary>
public enum WalletEntryKind
{
    TopUp = 0,
    Payment = 1,
    Refund = 2,
    Withdrawal = 3,
    Adjustment = 4
}

/// <summary>One change of the balance: signed (credit positive), with the balance it left and what caused it.</summary>
public sealed record WalletEntry(
    long Id, int CustomerId, WalletEntryKind Kind, decimal Amount, decimal BalanceAfter, string? ReferenceType, int? ReferenceId, string? Note,
    DateTime CreatedOnUtc);

/// <summary>A change to post: signed, with a key that makes posting it twice post it once.</summary>
public sealed record WalletPosting(
    int CustomerId, WalletEntryKind Kind, decimal Amount, string IdempotencyKey, string? ReferenceType, int? ReferenceId, string? Note, DateTime NowUtc);

public enum WalletPostOutcome
{
    Posted = 0,

    /// <summary>This key was posted before; nothing changed this time.</summary>
    AlreadyPosted = 1,

    /// <summary>A debit larger than the balance; nothing changed.</summary>
    Insufficient = 2
}

public sealed record WalletPostResult(WalletPostOutcome Outcome, decimal Balance);

public static class WalletLimits
{
    public const decimal MinTopUp = 10_000m;
    public const decimal MaxTopUp = 50_000_000m;
    public const int MaxKeyLength = 100;
    public const int MaxPageSize = 100;
}

/// <summary>Business-rule codes of wallets (HTTP 409).</summary>
public static class WalletErrors
{
    public const string Insufficient = "wallet.insufficient";
    public const string MethodNotAllowed = "wallet.method_not_allowed";
}

public static class WalletRules
{
    public static string ToWire(WalletEntryKind kind) => kind switch
    {
        WalletEntryKind.TopUp => "top_up",
        WalletEntryKind.Payment => "payment",
        WalletEntryKind.Refund => "refund",
        WalletEntryKind.Withdrawal => "withdrawal",
        _ => "adjustment"
    };
}

public sealed record WalletTopUpCommand(decimal Amount, string? Method, string? IdempotencyKey);

public interface IWalletStore
{
    /// <summary>The wallet; a customer who never had one has a balance of zero.</summary>
    Task<decimal> GetBalanceAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<PagedResult<WalletEntry>> GetEntriesAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Posts the change and its ledger line in one transaction, the wallet row locked: a debit beyond the balance is refused, and a key that
    /// was posted before changes nothing.
    /// </summary>
    Task<WalletPostResult> PostAsync(WalletPosting posting, CancellationToken cancellationToken);
}

public interface IWalletService
{
    Task<Wallet> GetMineAsync(int customerId, CancellationToken cancellationToken);
    Task<PagedResult<WalletEntry>> GetEntriesAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// Starts a top-up: a payment of the wallet through a gateway with its own payment page. The money is added when the gateway says it
    /// was paid. The same key is the same top-up.
    /// </summary>
    Task<CatalogResult<Payments.PaymentTransaction>> StartTopUpAsync(int customerId, WalletTopUpCommand command, CancellationToken cancellationToken);

    /// <summary>Adds money (a refund); posting the same key again adds nothing.</summary>
    Task<WalletPostResult> CreditAsync(
        int customerId, WalletEntryKind kind, decimal amount, string idempotencyKey, string? referenceType, int? referenceId, string? note,
        CancellationToken cancellationToken);
}
