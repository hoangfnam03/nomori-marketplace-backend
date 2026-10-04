using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Cart;

public sealed class CartService(
    ICartStore cartStore,
    IProductStore productStore,
    IProductAttributeStore attributeStore,
    IVendorMemberStore memberStore,
    IPriceCalculationService priceService,
    IInventoryService inventoryService,
    IPrimaryCurrencyProvider primaryCurrency,
    IClock clock) : ICartService
{
    public Task<int> CountAsync(int customerId, CancellationToken cancellationToken) =>
        cartStore.CountUnitsAsync(customerId, cancellationToken);

    public async Task<CartView> GetAsync(int customerId, CancellationToken cancellationToken) =>
        await BuildViewAsync(customerId, cancellationToken);

    public async Task<CatalogResult<CartView>> AddAsync(int customerId, AddToCartCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (command.ProductId <= 0) errors["productId"] = ["Choose a product."];
        if (command.Quantity is < 1 or > CartLimits.MaxQuantity) errors["quantity"] = [$"The quantity must be between 1 and {CartLimits.MaxQuantity}."];
        if (errors.Count > 0) return CatalogResult.Failure<CartView>(errors);

        // Only products a customer can see exist for the cart.
        var product = await productStore.GetAsync(command.ProductId, cancellationToken);
        if (product is null || !product.IsVisibleAt(clock.UtcNow)) return CatalogResult.Error<CartView>(CatalogErrors.NotFound);

        // Buying from your own shop is refused: it would only inflate its own numbers.
        if (await memberStore.GetAsync(product.VendorId, customerId, cancellationToken) is not null)
            return CatalogResult.Error<CartView>(CartErrors.OwnProduct);

        var valueIds = (command.ValueIds ?? []).Distinct().ToList();
        var key = CartRules.ValueKey(valueIds);
        var existing = await cartStore.FindAsync(customerId, product.Id, key, cancellationToken);

        // The total of the line is what is priced and checked: tier prices and stock depend on it.
        var total = (existing?.Quantity ?? 0) + command.Quantity;
        if (total > CartLimits.MaxQuantity)
            return CatalogResult.Failure<CartView>("quantity", $"A line can hold at most {CartLimits.MaxQuantity} units.");
        if (existing is null && (await cartStore.GetLinesAsync(customerId, cancellationToken)).Count >= CartLimits.MaxLines)
            return CatalogResult.Error<CartView>(CartErrors.LineLimit);

        var checkedLine = await QuoteAndCheckAsync(product, valueIds, total, cancellationToken);
        if (checkedLine.Failure is not null) return CatalogResult.Failure<CartView>(checkedLine.Failure);
        var quote = checkedLine.Quote!;

        var now = clock.UtcNow;
        if (existing is not null)
        {
            await cartStore.UpdateLineAsync(existing.Id, total, quote.UnitPrice, now, cancellationToken);
        }
        else
        {
            var inserted = await cartStore.InsertAsync(new CartLine
            {
                CustomerId = customerId, ProductId = product.Id, ValueIds = key, Quantity = total,
                AddedUnitPrice = quote.UnitPrice, CreatedOnUtc = now, UpdatedOnUtc = now
            }, cancellationToken);

            // Two requests added the same choice at once: the other one won, so this one adds to its line.
            if (!inserted)
            {
                var winner = await cartStore.FindAsync(customerId, product.Id, key, cancellationToken);
                if (winner is not null)
                    await cartStore.UpdateLineAsync(winner.Id, Math.Min(winner.Quantity + command.Quantity, CartLimits.MaxQuantity), quote.UnitPrice, now, cancellationToken);
            }
        }
        return CatalogResult.Success(await BuildViewAsync(customerId, cancellationToken));
    }

    public async Task<CatalogResult<CartView>> SetQuantityAsync(int customerId, int lineId, int quantity, CancellationToken cancellationToken)
    {
        var line = await cartStore.GetLineAsync(customerId, lineId, cancellationToken);
        if (line is null) return CatalogResult.Error<CartView>(CatalogErrors.NotFound);
        if (quantity is < 1 or > CartLimits.MaxQuantity)
            return CatalogResult.Failure<CartView>("quantity", $"The quantity must be between 1 and {CartLimits.MaxQuantity}.");

        var product = await productStore.GetAsync(line.ProductId, cancellationToken);
        if (product is null || !product.IsVisibleAt(clock.UtcNow))
            return CatalogResult.Failure<CartView>("quantity", "This product is no longer available. Remove it from your cart.");

        var checkedLine = await QuoteAndCheckAsync(product, CartRules.ParseValueKey(line.ValueIds), quantity, cancellationToken);
        if (checkedLine.Failure is not null) return CatalogResult.Failure<CartView>(checkedLine.Failure);

        await cartStore.UpdateLineAsync(line.Id, quantity, checkedLine.Quote!.UnitPrice, clock.UtcNow, cancellationToken);
        return CatalogResult.Success(await BuildViewAsync(customerId, cancellationToken));
    }

    public async Task<CatalogResult<CartView>> RemoveAsync(int customerId, int lineId, CancellationToken cancellationToken)
    {
        if (!await cartStore.DeleteAsync(customerId, lineId, cancellationToken)) return CatalogResult.Error<CartView>(CatalogErrors.NotFound);
        return CatalogResult.Success(await BuildViewAsync(customerId, cancellationToken));
    }

    public async Task<CartView> ClearAsync(int customerId, CancellationToken cancellationToken)
    {
        await cartStore.ClearAsync(customerId, cancellationToken);
        return await BuildViewAsync(customerId, cancellationToken);
    }

    public async Task<CartView> AcceptPricesAsync(int customerId, CancellationToken cancellationToken)
    {
        var view = await BuildViewAsync(customerId, cancellationToken);
        foreach (var line in view.Groups.SelectMany(g => g.Lines).Where(l => l.PreviousUnitPrice is not null))
            await cartStore.SetAddedUnitPriceAsync(line.Id, line.UnitPrice, cancellationToken);
        return await BuildViewAsync(customerId, cancellationToken);
    }

    // ---- Helpers ----

    /// <summary>Prices the line for a quantity and checks the stock. Either a quote, or the field errors to return.</summary>
    private async Task<(PriceQuote? Quote, Dictionary<string, string[]>? Failure)> QuoteAndCheckAsync(
        Product product, IReadOnlyList<int> valueIds, int quantity, CancellationToken cancellationToken)
    {
        var result = await priceService.QuoteAsync(new PriceRequest(product.Id, quantity, valueIds), cancellationToken);
        if (!result.Succeeded)
        {
            return (null, result.Errors.Count > 0
                ? result.Errors.ToDictionary(e => e.Key, e => e.Value)
                : new Dictionary<string, string[]> { ["productId"] = ["This product is not available."] });
        }

        var availability = await inventoryService.GetAvailabilityAsync(product.Id, cancellationToken);
        if (availability is { TrackInventory: true })
        {
            var available = result.Value!.CombinationId is { } combinationId
                ? availability.Combinations.GetValueOrDefault(combinationId)
                : availability.Product;
            if (quantity > available)
            {
                var message = available <= 0 ? "This item is out of stock." : $"Only {available} available.";
                return (null, new Dictionary<string, string[]> { ["quantity"] = [message] });
            }
        }
        return (result.Value, null);
    }

    private async Task<CartView> BuildViewAsync(int customerId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var lines = await cartStore.GetLinesAsync(customerId, cancellationToken);
        var pictures = await productStore.GetMainPictureIdsAsync(lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken);

        var views = new List<CartLineView>();
        foreach (var line in lines.OrderBy(l => l.CreatedOnUtc).ThenBy(l => l.Id))
        {
            var product = await productStore.GetAsync(line.ProductId, cancellationToken);
            if (product is null) continue; // a deleted product silently leaves the cart

            var valueIds = CartRules.ParseValueKey(line.ValueIds);
            var visible = product.IsVisibleAt(now);
            PriceQuote? quote = null;
            if (visible)
            {
                var quoted = await priceService.QuoteAsync(new PriceRequest(product.Id, line.Quantity, valueIds), cancellationToken);
                quote = quoted.Succeeded ? quoted.Value : null;
            }

            var availability = visible ? await inventoryService.GetAvailabilityAsync(product.Id, cancellationToken) : null;
            var tracked = availability?.TrackInventory ?? false;
            int? available = null;
            if (tracked)
            {
                available = quote?.CombinationId is { } combinationId
                    ? availability!.Combinations.GetValueOrDefault(combinationId)
                    : availability!.Product;
            }

            var issues = CartRules.Issues(visible, quote is not null, tracked, available ?? 0, line.Quantity, quote?.UnitPrice, line.AddedUnitPrice);
            var (label, sku) = await DescribeAsync(product, valueIds, quote?.CombinationId, cancellationToken);

            views.Add(new CartLineView(
                line.Id, product.Id, product.Name, product.VendorId, product.VendorName, pictures.GetValueOrDefault(product.Id),
                label, sku, line.Quantity,
                quote?.UnitPrice ?? line.AddedUnitPrice, quote?.ComparePrice, quote?.LineTotal ?? 0m, quote?.AppliedRule,
                available, quote is not null && quote.UnitPrice != line.AddedUnitPrice ? line.AddedUnitPrice : null,
                issues, quote?.CombinationId, line.ValueIds));
        }

        // One group per shop, in the order the shops first appear in the cart.
        var groups = views.GroupBy(v => v.VendorId)
            .Select(g => new CartShopGroup(g.Key, g.First().VendorName, g.ToList(), g.Sum(v => v.LineTotal)))
            .ToList();

        return new CartView(currency.Code, groups, groups.Sum(g => g.Subtotal), views.Sum(v => v.Quantity), CartRules.CanCheckout(views));
    }

    /// <summary>The variant label ("Red / S") in the order of the options, and the SKU of the combination or the product.</summary>
    private async Task<(string? Label, string? Sku)> DescribeAsync(
        Product product, int[] valueIds, int? combinationId, CancellationToken cancellationToken)
    {
        if (valueIds.Length == 0) return (null, product.Sku);

        var mappings = (await attributeStore.GetMappingsAsync(product.Id, cancellationToken)).OrderBy(m => m.DisplayOrder).ToList();
        var values = (await attributeStore.GetValuesByProductAsync(product.Id, cancellationToken)).ToDictionary(v => v.Id);
        var names = valueIds.Where(values.ContainsKey).Select(id => values[id])
            .OrderBy(v => mappings.FindIndex(m => m.Id == v.ProductAttributeMappingId)).Select(v => v.Name).ToList();
        var label = names.Count > 0 ? string.Join(" / ", names) : null;

        var combination = combinationId is { } id
            ? (await attributeStore.GetCombinationsAsync(product.Id, cancellationToken)).FirstOrDefault(c => c.Id == id)
            : null;
        return (label, combination?.Sku ?? product.Sku);
    }
}
