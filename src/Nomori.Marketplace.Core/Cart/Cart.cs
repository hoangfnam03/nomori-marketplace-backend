using Nomori.Marketplace.Core.Catalog;

namespace Nomori.Marketplace.Core.Cart;

public static class CartLimits
{
    public const int MaxLines = 50;
    public const int MaxQuantity = PricingLimits.MaxQuantity;
}

/// <summary>Business-rule codes of the cart (HTTP 409). Not found is <see cref="CatalogErrors.NotFound"/>.</summary>
public static class CartErrors
{
    public const string LineLimit = "cart.line_limit";
    public const string OwnProduct = "cart.own_product";
}

/// <summary>What can be wrong with a line when the cart is shown. All but <see cref="PriceChanged"/> block checkout.</summary>
public static class CartIssues
{
    public const string Unavailable = "unavailable";
    public const string VariantUnavailable = "variant_unavailable";
    public const string OutOfStock = "out_of_stock";
    public const string InsufficientStock = "insufficient_stock";
    public const string PriceChanged = "price_changed";
}

public sealed class CartLine
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int ProductId { get; set; }

    /// <summary>The chosen variant value ids, sorted and comma separated; empty for a plain product. See <see cref="CartRules.ValueKey"/>.</summary>
    public string ValueIds { get; set; } = string.Empty;

    public int Quantity { get; set; }

    /// <summary>The unit price when the line was added or last changed, to tell the customer when it changes.</summary>
    public decimal AddedUnitPrice { get; set; }

    public DateTime CreatedOnUtc { get; set; }
    public DateTime UpdatedOnUtc { get; set; }
}

public static class CartRules
{
    /// <summary>The identity of a variant choice: distinct ids, sorted, comma separated.</summary>
    public static string ValueKey(IEnumerable<int>? valueIds) =>
        string.Join(',', (valueIds ?? []).Distinct().OrderBy(id => id));

    public static int[] ParseValueKey(string key) =>
        key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    /// <summary>The issues of one line, in the order they matter. A product that is gone or a variant that is gone has nothing else to check.</summary>
    public static IReadOnlyList<string> Issues(
        bool visible, bool priced, bool tracked, int available, int quantity, decimal? unitPrice, decimal addedUnitPrice)
    {
        if (!visible) return [CartIssues.Unavailable];
        if (!priced) return [CartIssues.VariantUnavailable];

        var issues = new List<string>();
        if (tracked)
        {
            if (available <= 0) issues.Add(CartIssues.OutOfStock);
            else if (available < quantity) issues.Add(CartIssues.InsufficientStock);
        }
        if (unitPrice is { } current && current != addedUnitPrice) issues.Add(CartIssues.PriceChanged);
        return issues;
    }

    public static bool IsBlocking(string issue) => issue != CartIssues.PriceChanged;

    public static bool CanCheckout(IReadOnlyList<CartLineView> lines) =>
        lines.Count > 0 && lines.All(l => !l.Issues.Any(IsBlocking));

    /// <summary>
    /// The cart cut down to the lines the customer chose to buy now, with its totals recomputed. Null or empty keeps the whole cart.
    /// Ids that are not in the cart are ignored here; checkout reports them.
    /// </summary>
    public static CartView Narrow(CartView cart, IReadOnlyCollection<int>? lineIds)
    {
        if (lineIds is not { Count: > 0 }) return cart;
        var wanted = lineIds.ToHashSet();
        var groups = cart.Groups
            .Select(g => g with { Lines = g.Lines.Where(l => wanted.Contains(l.Id)).ToList() })
            .Where(g => g.Lines.Count > 0)
            .Select(g => g with { Subtotal = g.Lines.Sum(l => l.LineTotal) })
            .ToList();
        var lines = groups.SelectMany(g => g.Lines).ToList();
        return cart with { Groups = groups, Subtotal = groups.Sum(g => g.Subtotal), ItemCount = lines.Sum(l => l.Quantity), CanCheckout = CanCheckout(lines) };
    }

    /// <summary>The chosen ids that are not lines of the cart (bought from another tab, or removed).</summary>
    public static IReadOnlyList<int> Missing(CartView cart, IReadOnlyCollection<int>? lineIds) =>
        lineIds is not { Count: > 0 } ? [] : lineIds.Distinct().Except(cart.Groups.SelectMany(g => g.Lines).Select(l => l.Id)).ToList();
}

public sealed record CartLineView(
    int Id,
    int ProductId,
    string Name,
    int VendorId,
    string? VendorName,
    int MainPictureId,
    string? VariantLabel,
    string? Sku,
    int Quantity,
    decimal UnitPrice,
    decimal? ComparePrice,
    decimal LineTotal,
    string? AppliedRule,
    /// <summary>What can still be bought; null when the product does not track stock.</summary>
    int? AvailableQuantity,
    /// <summary>The unit price the customer saw earlier, only when it differs from <see cref="UnitPrice"/>.</summary>
    decimal? PreviousUnitPrice,
    IReadOnlyList<string> Issues);

public sealed record CartShopGroup(int VendorId, string? VendorName, IReadOnlyList<CartLineView> Lines, decimal Subtotal);

public sealed record CartView(
    string CurrencyCode,
    IReadOnlyList<CartShopGroup> Groups,
    decimal Subtotal,
    int ItemCount,
    bool CanCheckout);

public sealed record AddToCartCommand(int ProductId, int Quantity, IReadOnlyList<int>? ValueIds);

public interface ICartStore
{
    Task<IReadOnlyList<CartLine>> GetLinesAsync(int customerId, CancellationToken cancellationToken);
    Task<CartLine?> GetLineAsync(int customerId, int lineId, CancellationToken cancellationToken);
    Task<CartLine?> FindAsync(int customerId, int productId, string valueIds, CancellationToken cancellationToken);

    /// <summary>Adds a line. Returns false (and adds nothing) when the same product and choice is already in the cart.</summary>
    Task<bool> InsertAsync(CartLine line, CancellationToken cancellationToken);

    Task UpdateLineAsync(int lineId, int quantity, decimal addedUnitPrice, DateTime nowUtc, CancellationToken cancellationToken);
    Task SetAddedUnitPriceAsync(int lineId, decimal addedUnitPrice, CancellationToken cancellationToken);

    /// <summary>True when a line of this customer was removed.</summary>
    Task<bool> DeleteAsync(int customerId, int lineId, CancellationToken cancellationToken);
    Task ClearAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>Total of the quantities of all lines.</summary>
    Task<int> CountUnitsAsync(int customerId, CancellationToken cancellationToken);
}

public interface ICartService
{
    Task<CartView> GetAsync(int customerId, CancellationToken cancellationToken);
    Task<int> CountAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>Adds units of a product (and variant choice) to the cart, or adds to the quantity of the existing line.</summary>
    Task<CatalogResult<CartView>> AddAsync(int customerId, AddToCartCommand command, CancellationToken cancellationToken);

    Task<CatalogResult<CartView>> SetQuantityAsync(int customerId, int lineId, int quantity, CancellationToken cancellationToken);
    Task<CatalogResult<CartView>> RemoveAsync(int customerId, int lineId, CancellationToken cancellationToken);
    Task<CartView> ClearAsync(int customerId, CancellationToken cancellationToken);

    /// <summary>Saves the current unit prices as the ones the customer has seen.</summary>
    Task<CartView> AcceptPricesAsync(int customerId, CancellationToken cancellationToken);
}
