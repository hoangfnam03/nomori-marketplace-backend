using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Orders;

namespace Nomori.Marketplace.Core.Returns;

/// <summary>Settings of returns (section <c>Returns</c>).</summary>
public sealed class ReturnOptions
{
    public const string SectionName = "Returns";

    /// <summary>A customer may ask to return goods this many days after the shop marked the order delivered.</summary>
    public int WindowDays { get; init; } = 14;
}

/// <summary>Business-rule codes of returns (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class ReturnErrors
{
    /// <summary>The shop order is not delivered, or the return window is over.</summary>
    public const string NotEligible = "return.not_eligible";

    public const string InvalidTransition = "return.invalid_transition";

    /// <summary>More of a line than was bought, counting the returns already open for it.</summary>
    public const string QuantityExceeded = "return.quantity_exceeded";

    /// <summary>No paid payment of the order can take this refund back. Refund by hand and say so (<c>manual</c>).</summary>
    public const string NoRefundablePayment = "return.no_refundable_payment";
}

/// <summary>The stored value is part of the database contract: add at the end, never renumber.</summary>
public enum ReturnStatus
{
    Requested = 0,
    Approved = 1,
    Rejected = 2,
    Received = 3,
    Refunded = 4,
    Withdrawn = 5
}

public enum ReturnAction
{
    Approve = 0,
    Reject = 1,
    Receive = 2,
    Refund = 3,
    Withdraw = 4
}

public static class ReturnReasons
{
    public const string Damaged = "damaged";
    public const string WrongItem = "wrong_item";
    public const string NotAsDescribed = "not_as_described";
    public const string ChangedMind = "changed_mind";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Damaged, WrongItem, NotAsDescribed, ChangedMind, Other];
}

public static class ReturnLimits
{
    public const int MaxNoteLength = 500;
    public const int MaxLines = 50;
    public const int MaxPageSize = 100;
}

public sealed class ReturnRequest
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public int ShopOrderId { get; set; }
    public int OrderId { get; set; }
    public string ShopOrderNumber { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public string ShopName { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public ReturnStatus Status { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? CustomerNote { get; set; }

    /// <summary>What the shop or administrator wrote when deciding. Shown to the customer.</summary>
    public string? ResolutionNote { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>What the customer gets back for these lines, worked out when the request was made (see <see cref="ReturnRules.RefundFor"/>). Shipping is never refunded.</summary>
    public decimal RefundAmount { get; set; }

    public bool Restocked { get; set; }

    /// <summary>The payment the refund went through; null before the refund and for a refund made by hand.</summary>
    public int? PaymentId { get; set; }

    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
    public List<ReturnLine> Lines { get; set; } = [];
}

public sealed class ReturnLine
{
    public int Id { get; set; }
    public int ReturnRequestId { get; set; }
    public int OrderLineId { get; set; }

    /// <summary>Copied from the order line, so the return reads the same even when the product is renamed or removed.</summary>
    public string Name { get; set; } = string.Empty;

    public string? VariantLabel { get; set; }
    public int ProductId { get; set; }
    public int? CombinationId { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
}

public sealed record ReturnLineRequest(int OrderLineId, int Quantity);

public sealed record RequestReturnCommand(int ShopOrderId, string? Reason, string? Note, IReadOnlyList<ReturnLineRequest>? Lines);

/// <summary>Who is acting on a return. A shop member acts for one shop; an administrator for any.</summary>
public sealed record ReturnCaller(OrderActor Actor, int ActorCustomerId, int? VendorId);

public sealed record ReturnQuery(int? CustomerId, int? VendorId, ReturnStatus? Status, int Page, int PageSize);

/// <summary>A change of status to apply in one compare-and-set.</summary>
public sealed record ReturnTransition(
    int Id, ReturnStatus From, ReturnStatus To, string? ResolutionNote, bool? Restocked, int? PaymentId, DateTime NowUtc);

public static class ReturnRules
{
    /// <summary>The status an action leads to, or null when this actor may not do it from this status.</summary>
    public static ReturnStatus? Transition(ReturnStatus from, ReturnAction action, OrderActor actor) => (action, from, actor) switch
    {
        (ReturnAction.Approve, ReturnStatus.Requested, OrderActor.Shop or OrderActor.Admin) => ReturnStatus.Approved,
        (ReturnAction.Reject, ReturnStatus.Requested, OrderActor.Shop or OrderActor.Admin) => ReturnStatus.Rejected,
        (ReturnAction.Receive, ReturnStatus.Approved, OrderActor.Shop or OrderActor.Admin) => ReturnStatus.Received,
        // The money is the platform's: only an administrator sends it back.
        (ReturnAction.Refund, ReturnStatus.Received, OrderActor.Admin) => ReturnStatus.Refunded,
        (ReturnAction.Withdraw, ReturnStatus.Requested, OrderActor.Customer) => ReturnStatus.Withdrawn,
        _ => null
    };

    /// <summary>A rejected or withdrawn return frees its quantities; every other status keeps them.</summary>
    public static bool HoldsQuantity(ReturnStatus status) => status is not (ReturnStatus.Rejected or ReturnStatus.Withdrawn);

    /// <summary>Delivered or completed, and not longer ago than the window since the shop marked it delivered.</summary>
    public static bool IsEligible(ShopOrderStatus status, DateTime? deliveredOnUtc, DateTime nowUtc, int windowDays) =>
        status is ShopOrderStatus.Delivered or ShopOrderStatus.Completed
        && deliveredOnUtc is { } delivered && nowUtc <= delivered.AddDays(Math.Max(1, windowDays));

    /// <summary>
    /// The price paid for <paramref name="quantity"/> units of a line: the unit price, minus the line's share of the shop's discount (by value),
    /// plus the tax charged on those units. Shipping is not part of it. Never negative.
    /// </summary>
    public static decimal RefundFor(OrderLine line, int quantity, ShopOrder shopOrder, int decimalPlaces)
    {
        var value = line.UnitPrice * quantity;
        var discountShare = shopOrder.Subtotal > 0 ? shopOrder.DiscountAmount * value / shopOrder.Subtotal : 0m;
        var taxShare = line.Quantity > 0 ? line.TaxAmount * quantity / line.Quantity : 0m;
        return Math.Max(0m, Math.Round(value - discountShare + taxShare, decimalPlaces, MidpointRounding.AwayFromZero));
    }

    public static string NumberFor(DateTime createdOnUtc, int id) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"RT{createdOnUtc:yyMMdd}-{id:D4}");

    public static string ToWire(ReturnStatus status) => status switch
    {
        ReturnStatus.Requested => "requested",
        ReturnStatus.Approved => "approved",
        ReturnStatus.Rejected => "rejected",
        ReturnStatus.Received => "received",
        ReturnStatus.Refunded => "refunded",
        _ => "withdrawn"
    };

    public static bool TryParseWire(string? value, out ReturnStatus status)
    {
        foreach (var candidate in Enum.GetValues<ReturnStatus>())
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
}

public interface IReturnStore
{
    /// <summary>
    /// Writes the request and its lines, and the number, in one transaction, after checking inside it that no line is returned more often than
    /// it was bought. False (and nothing written) when a quantity would be exceeded.
    /// </summary>
    Task<(ReturnRequest? Request, bool QuantityExceeded)> InsertAsync(ReturnRequest request, CancellationToken cancellationToken);

    /// <summary>Quantity held by returns that are not rejected or withdrawn, per order line of the shop order.</summary>
    Task<IReadOnlyDictionary<int, int>> HeldQuantitiesAsync(int shopOrderId, CancellationToken cancellationToken);

    Task<ReturnRequest?> GetAsync(int id, CancellationToken cancellationToken);

    /// <summary>Requests without lines, newest first.</summary>
    Task<PagedResult<ReturnRequest>> GetPagedAsync(ReturnQuery query, CancellationToken cancellationToken);

    /// <summary>Compare-and-set of the status. False means the request was no longer in the expected status.</summary>
    Task<bool> TryTransitionAsync(ReturnTransition transition, CancellationToken cancellationToken);

    /// <summary>Paid or partly refunded payments of the order, newest first.</summary>
    Task<IReadOnlyList<int>> PaymentIdsForOrderAsync(int orderId, CancellationToken cancellationToken);
}

public interface IReturnService
{
    // ---- Customers ----

    Task<CatalogResult<ReturnRequest>> RequestAsync(int customerId, RequestReturnCommand command, CancellationToken cancellationToken);
    Task<PagedResult<ReturnRequest>> GetMineAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CatalogResult<ReturnRequest>> GetMineAsync(int customerId, int id, CancellationToken cancellationToken);
    Task<CatalogResult<ReturnRequest>> WithdrawAsync(int customerId, int id, CancellationToken cancellationToken);

    // ---- Shops and administrators ----

    /// <summary>A shop sees only its own returns: <see cref="ReturnQuery.VendorId"/> is taken from the caller. An administrator sees all.</summary>
    Task<PagedResult<ReturnRequest>> GetAsync(ReturnCaller caller, ReturnStatus? status, int page, int pageSize, CancellationToken cancellationToken);

    Task<CatalogResult<ReturnRequest>> GetAsync(ReturnCaller caller, int id, CancellationToken cancellationToken);
    Task<CatalogResult<ReturnRequest>> ApproveAsync(ReturnCaller caller, int id, string? note, CancellationToken cancellationToken);

    /// <summary>The reason is required: the customer reads it.</summary>
    Task<CatalogResult<ReturnRequest>> RejectAsync(ReturnCaller caller, int id, string? note, CancellationToken cancellationToken);

    /// <summary>The goods came back. <paramref name="restock"/> puts the units back on sale; leave it off for goods that cannot be sold again.</summary>
    Task<CatalogResult<ReturnRequest>> ReceiveAsync(ReturnCaller caller, int id, bool restock, CancellationToken cancellationToken);

    // ---- Administrators ----

    /// <summary>
    /// Sends the money back through the payment of the order. <paramref name="manual"/> records a refund made outside the system (cash on
    /// delivery, a bank transfer): nothing is sent to a gateway.
    /// </summary>
    Task<CatalogResult<ReturnRequest>> RefundAsync(ReturnCaller caller, int id, bool manual, CancellationToken cancellationToken);
}
