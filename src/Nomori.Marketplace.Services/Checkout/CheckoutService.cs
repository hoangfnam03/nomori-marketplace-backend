using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Checkout;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;

namespace Nomori.Marketplace.Services.Checkout;

public sealed class CheckoutService(
    ICartService cartService,
    ICartStore cartStore,
    IPriceCalculationService priceService,
    IShippingService shippingService,
    IPaymentService paymentService,
    IPaymentStore paymentStore,
    IOrderService orderService,
    IInventoryService inventoryService,
    ICustomerAccountDataService addressService,
    IAuditLogService auditLog) : ICheckoutService
{
    /// <summary>Everything the preview and the placement both need, worked out once from the same inputs.</summary>
    private sealed record State(
        CartView Cart, CustomerAddress? Address, IReadOnlyList<CheckoutShopShipping> Shops, IReadOnlyList<PaymentMethodView> Methods,
        PaymentMethodView? Method, IReadOnlyList<string> Problems, decimal? ShippingTotal);

    /// <summary>A line as it will be sold: what to take from stock and what to write on the order.</summary>
    private sealed record SaleLine(int VendorId, NewOrderLine Line, bool Tracked);

    // ---- Preview ----

    public async Task<CheckoutPreview> PreviewAsync(int customerId, CheckoutChoices choices, CancellationToken cancellationToken)
    {
        var state = await BuildAsync(customerId, choices, cancellationToken);
        var total = state.ShippingTotal is { } shipping ? state.Cart.Subtotal + shipping : (decimal?)null;
        return new CheckoutPreview(
            state.Cart, state.Address?.Id, state.Shops, state.Methods, state.Method?.SystemName, state.Cart.Subtotal, state.ShippingTotal, total,
            state.Problems, state.Problems.Count == 0);
    }

    // ---- Place ----

    public async Task<CatalogResult<PlacedOrder>> PlaceAsync(int customerId, PlaceOrderRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var key = request.IdempotencyKey?.Trim();
        if (!CheckoutRules.IsValidKey(key))
            errors["idempotencyKey"] = [$"The key must have {CheckoutLimits.MinKeyLength} to {CheckoutLimits.MaxKeyLength} letters, digits, '-' or '_'."];
        var note = request.Note?.Trim();
        if (note?.Length > CheckoutLimits.MaxNoteLength) errors["note"] = [$"The note can have at most {CheckoutLimits.MaxNoteLength} characters."];
        if (errors.Count > 0) return CatalogResult.Failure<PlacedOrder>(errors);

        // The same key again is the same request: answer with what it made and take nothing twice.
        var placementKey = CheckoutRules.PlacementKey(customerId, key!);
        if (await orderService.FindByPlacementKeyAsync(placementKey, cancellationToken) is { } earlier)
            return await ReplayAsync(earlier, request.PaymentMethod, cancellationToken);

        var state = await BuildAsync(customerId, new CheckoutChoices(request.AddressId, request.ShippingChoices, request.PaymentMethod), cancellationToken);

        // Problems with the cart come first (409): the customer has to go back to the cart. Then what the customer chose (400).
        if (state.Problems.Contains(CheckoutProblems.CartEmpty) || state.Problems.Contains(CheckoutProblems.CartIssues))
            return CatalogResult.Error<PlacedOrder>(CheckoutErrors.CartNotReady);
        if (state.Problems.Contains(CheckoutProblems.PricesChanged)) return CatalogResult.Error<PlacedOrder>(CheckoutErrors.PricesChanged);

        foreach (var problem in state.Problems.Where(p => !CheckoutRules.IsCartProblem(p)))
            errors[CheckoutRules.FieldOf(problem)] = [CheckoutRules.MessageOf(problem)];
        if (!request.AcceptedTerms) errors["acceptedTerms"] = ["You have to accept the terms to place the order."];
        if (errors.Count > 0) return CatalogResult.Failure<PlacedOrder>(errors);

        var sale = await BuildSaleAsync(customerId, state, cancellationToken);
        if (sale.Failure is not null) return CatalogResult.Failure<PlacedOrder>(sale.Failure);
        if (sale.Lines is null) return CatalogResult.Error<PlacedOrder>(CheckoutErrors.CartNotReady);

        // 1. Take the stock first: it is the thing that can run out.
        var reference = CheckoutRules.StockReference(placementKey);
        var taken = await TakeStockAsync(reference, sale.Lines, cancellationToken);
        if (taken is not null)
        {
            // Another request with this key may have won the race; its order is the answer.
            if (await orderService.FindByPlacementKeyAsync(placementKey, cancellationToken) is { } winner)
                return await ReplayAsync(winner, request.PaymentMethod, cancellationToken);
            return CatalogResult.Error<PlacedOrder>(taken);
        }

        // 2. Make the order. Whatever goes wrong from here, the stock goes back.
        var rollbackReference = "checkout-rollback:" + placementKey;
        var stockLines = sale.Lines.Select(l => new StockReturnLine(l.Line.ProductId, l.Line.CombinationId, l.Line.Quantity)).ToList();
        CatalogResult<Order> created;
        try
        {
            created = await orderService.CreateAsync(BuildCommand(customerId, placementKey, note, state, sale.Lines), cancellationToken);
        }
        catch
        {
            await inventoryService.ReturnToStockAsync(rollbackReference, stockLines, CancellationToken.None);
            throw;
        }
        if (!created.Succeeded)
        {
            await inventoryService.ReturnToStockAsync(rollbackReference, stockLines, cancellationToken);
            return new CatalogResult<PlacedOrder>(null, created.Errors, created.ErrorCode);
        }
        var order = created.Value!;

        // 3. Make the payment for the total of the order. If the provider refuses, the order is cancelled and the stock goes back.
        var paid = await paymentService.CreateAsync(
            new CreatePaymentCommand(PaymentReferenceTypes.Order, order.Id, CheckoutRules.PaymentKey(order.Id), order.PaymentMethod, order.Total, customerId), cancellationToken);
        if (!paid.Succeeded)
        {
            await orderService.CancelAsSystemAsync(order.Id, "Payment failed", cancellationToken);
            return CatalogResult.Error<PlacedOrder>(CheckoutErrors.PaymentFailed);
        }

        // 4. Last: empty the cart, so a failure above leaves it as it was.
        await cartService.ClearAsync(customerId, cancellationToken);

        await auditLog.WriteAsync("checkout.placed", customerId, entityType: "Order", entityId: order.Id,
            details: new { orderId = order.Id, order.Number, order.Total, order.CurrencyCode, order.PaymentMethod, acceptedTerms = true },
            cancellationToken: cancellationToken);
        return CatalogResult.Success(new PlacedOrder(order, paid.Value, false));
    }

    // ---- Helpers ----

    private async Task<CatalogResult<PlacedOrder>> ReplayAsync(Order existing, string? paymentMethod, CancellationToken cancellationToken)
    {
        if (!string.Equals(existing.PaymentMethod, paymentMethod?.Trim(), StringComparison.OrdinalIgnoreCase))
            return CatalogResult.Error<PlacedOrder>(OrderErrors.PlacementConflict);

        var payment = await paymentStore.GetByKeyAsync(CheckoutRules.PaymentKey(existing.Id), cancellationToken);
        return CatalogResult.Success(new PlacedOrder(existing, payment, true));
    }

    private async Task<State> BuildAsync(int customerId, CheckoutChoices choices, CancellationToken cancellationToken)
    {
        var cart = await cartService.GetAsync(customerId, cancellationToken);
        var problems = new List<string>(CheckoutRules.CartProblems(cart));

        var methods = await paymentService.GetAvailableMethodsAsync(cancellationToken);
        PaymentMethodView? method = null;
        if (string.IsNullOrWhiteSpace(choices.PaymentMethod)) problems.Add(CheckoutProblems.PaymentRequired);
        else if ((method = methods.FirstOrDefault(m => string.Equals(m.SystemName, choices.PaymentMethod.Trim(), StringComparison.OrdinalIgnoreCase))) is null)
            problems.Add(CheckoutProblems.PaymentInvalid);

        // Only the customer's own saved addresses exist for checkout.
        CustomerAddress? address = null;
        if (choices.AddressId is not { } addressId) problems.Add(CheckoutProblems.AddressRequired);
        else if ((address = (await addressService.GetAddressesAsync(customerId, cancellationToken)).FirstOrDefault(a => a.Id == addressId)) is null)
            problems.Add(CheckoutProblems.AddressInvalid);

        ShippingQuote? quote = null;
        if (address is not null && cart.Groups.Count > 0)
        {
            var quoted = await shippingService.QuoteAsync(customerId, new ShippingQuoteRequest(address.Id, null, null), cancellationToken);
            if (quoted.Succeeded) quote = quoted.Value;
            else problems.Add(CheckoutProblems.AddressInvalid);
        }

        var shops = new List<CheckoutShopShipping>();
        var choiceList = choices.ShippingChoices ?? [];
        foreach (var group in cart.Groups)
        {
            var options = quote?.Shops.FirstOrDefault(s => s.VendorId == group.VendorId)?.Options ?? [];
            ShippingOption? chosen = null;
            if (quote is not null)
            {
                if (options.Count == 0)
                {
                    problems.Add(CheckoutProblems.ShippingUnavailable);
                }
                else if (choiceList.FirstOrDefault(c => c.VendorId == group.VendorId) is not { } choice)
                {
                    problems.Add(CheckoutProblems.ShippingNotChosen);
                }
                else if ((chosen = options.FirstOrDefault(o => o.RateId == choice.RateId)) is null)
                {
                    problems.Add(CheckoutProblems.ShippingInvalid);
                }
            }
            shops.Add(new CheckoutShopShipping(group.VendorId, group.VendorName, group.Subtotal, options, chosen));
        }
        // A choice for a shop that is not in the cart is a mistake of the caller, not something to ignore.
        if (choiceList.Any(c => cart.Groups.All(g => g.VendorId != c.VendorId))) problems.Add(CheckoutProblems.ShippingInvalid);

        decimal? shippingTotal = quote is not null && shops.Count > 0 && shops.All(s => s.Chosen is not null) ? shops.Sum(s => s.Chosen!.Fee) : null;
        return new State(cart, address, shops, methods, method, problems.Distinct().ToList(), shippingTotal);
    }

    /// <summary>Prices every line again and checks that it can be sold. Null lines mean the cart changed under us.</summary>
    private async Task<(IReadOnlyList<SaleLine>? Lines, Dictionary<string, string[]>? Failure)> BuildSaleAsync(
        int customerId, State state, CancellationToken cancellationToken)
    {
        var raw = await cartStore.GetLinesAsync(customerId, cancellationToken);
        var lines = new List<SaleLine>();
        foreach (var group in state.Cart.Groups)
        {
            foreach (var view in group.Lines)
            {
                if (view.Quantity > CheckoutLimits.MaxLineQuantity)
                {
                    return (null, new Dictionary<string, string[]>
                    {
                        ["cart"] = [$"At most {CheckoutLimits.MaxLineQuantity} units of '{view.Name}' can be bought in one order. Lower the quantity in your cart."]
                    });
                }

                var line = raw.FirstOrDefault(l => l.Id == view.Id);
                if (line is null) return (null, null);

                var quote = await priceService.QuoteAsync(new PriceRequest(line.ProductId, line.Quantity, CartRules.ParseValueKey(line.ValueIds)), cancellationToken);
                var availability = await inventoryService.GetAvailabilityAsync(line.ProductId, cancellationToken);
                if (!quote.Succeeded || availability is null) return (null, null);

                lines.Add(new SaleLine(group.VendorId,
                    new NewOrderLine(line.ProductId, quote.Value!.CombinationId, view.Name, view.VariantLabel, view.Sku, view.MainPictureId, line.Quantity, quote.Value.UnitPrice),
                    availability.TrackInventory));
            }
        }
        return (lines, null);
    }

    /// <summary>Reserves every line, then commits them in one transaction. Null when the stock is taken; otherwise the error code, with nothing held.</summary>
    private async Task<string?> TakeStockAsync(string reference, IReadOnlyList<SaleLine> lines, CancellationToken cancellationToken)
    {
        foreach (var sale in lines)
        {
            var reserved = await inventoryService.ReserveAsync(
                reference, sale.Line.ProductId, sale.Line.CombinationId, sale.Line.Quantity, CheckoutLimits.ReservationMinutes, cancellationToken);
            if (!reserved.Succeeded)
            {
                await inventoryService.ReleaseAsync(reference, cancellationToken);
                return reserved.ErrorCode ?? CheckoutErrors.CartNotReady;
            }
        }

        // Products without a stock limit hold nothing, so with only those there is nothing to commit.
        if (lines.All(l => !l.Tracked)) return null;

        var committed = await inventoryService.CommitAsync(reference, cancellationToken);
        if (committed.Succeeded) return null;

        await inventoryService.ReleaseAsync(reference, cancellationToken);
        return committed.ErrorCode ?? CatalogErrors.InsufficientStock;
    }

    private static NewOrderCommand BuildCommand(int customerId, string placementKey, string? note, State state, IReadOnlyList<SaleLine> lines)
    {
        var address = state.Address!;
        var recipient = new NewOrderRecipient(
            $"{address.FirstName} {address.LastName}".Trim(), address.PhoneNumber, address.Address1, address.Address2, address.City,
            address.StateProvince, address.ZipPostalCode, address.CountryCode);

        var shops = state.Shops.Select(shop => new NewShopOrder(
            shop.VendorId, shop.Chosen!.Name, shop.Chosen.RateId, shop.Chosen.Fee,
            lines.Where(l => l.VendorId == shop.VendorId).Select(l => l.Line).ToList())).ToList();

        return new NewOrderCommand(customerId, placementKey, state.Method!.SystemName, string.IsNullOrEmpty(note) ? null : note, recipient, shops);
    }
}
