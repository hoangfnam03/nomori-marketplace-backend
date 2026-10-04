using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Orders;

public sealed class OrderService(
    IOrderStore store,
    IVendorStore vendorStore,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : IOrderService
{
    // ---- Checkout ----

    public async Task<CatalogResult<Order>> CreateAsync(NewOrderCommand command, CancellationToken cancellationToken)
    {
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        var errors = new Dictionary<string, string[]>();
        var key = command.PlacementKey?.Trim() ?? string.Empty;
        var method = command.PaymentMethod?.Trim() ?? string.Empty;
        var note = command.CustomerNote?.Trim();

        if (command.CustomerId <= 0) errors["customerId"] = ["The order needs a customer."];
        if (key.Length is 0 or > OrderLimits.MaxKeyLength) errors["placementKey"] = [$"The key must have 1 to {OrderLimits.MaxKeyLength} characters."];
        if (method.Length is 0 or > 50) errors["paymentMethod"] = ["Choose a payment method."];
        if (note?.Length > OrderLimits.MaxNoteLength) errors["customerNote"] = [$"The note can have at most {OrderLimits.MaxNoteLength} characters."];
        ValidateRecipient(command.Recipient, errors);

        var shops = command.Shops ?? [];
        if (shops.Count is 0 or > OrderLimits.MaxShops) errors["shops"] = [$"An order has 1 to {OrderLimits.MaxShops} shops."];
        else if (shops.Select(s => s.VendorId).Distinct().Count() != shops.Count) errors["shops"] = ["A shop can appear only once."];

        var vendors = new Dictionary<int, Vendor>();
        foreach (var shop in shops)
        {
            var vendor = await vendorStore.GetAsync(shop.VendorId, cancellationToken);
            if (vendor is null) errors["shops"] = ["A shop in the order does not exist."];
            else vendors[vendor.Id] = vendor;
            ValidateShop(shop, currency.DecimalPlaces, errors);
        }
        if (errors.Count > 0) return CatalogResult.Failure<Order>(errors);

        var now = clock.UtcNow;
        var recipient = command.Recipient!;
        var order = new Order
        {
            CustomerId = command.CustomerId, PlacementKey = key, CurrencyCode = currency.Code, PaymentMethod = method,
            CustomerNote = string.IsNullOrEmpty(note) ? null : note,
            RecipientName = recipient.Name!.Trim(), RecipientPhone = recipient.Phone!.Trim(), Address1 = recipient.Address1!.Trim(),
            Address2 = Clean(recipient.Address2), City = recipient.City!.Trim(), StateProvince = Clean(recipient.StateProvince),
            PostalCode = Clean(recipient.PostalCode), CountryCode = recipient.CountryCode!.Trim().ToUpperInvariant(), CreatedOnUtc = now
        };

        // Every total is computed here from prices and quantities; nothing a caller adds up is trusted.
        foreach (var shop in shops)
        {
            var lines = shop.Lines!.Select(l => new OrderLine
            {
                ProductId = l.ProductId, CombinationId = l.CombinationId, Name = l.Name!.Trim(), VariantLabel = Clean(l.VariantLabel), Sku = Clean(l.Sku),
                PictureId = l.PictureId, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                LineTotal = OrderRules.LineTotal(l.UnitPrice, l.Quantity, currency.DecimalPlaces)
            }).ToList();
            var subtotal = lines.Sum(l => l.LineTotal);
            order.ShopOrders.Add(new ShopOrder
            {
                VendorId = shop.VendorId, ShopName = vendors[shop.VendorId].Name, Status = ShopOrderStatus.Pending, Subtotal = subtotal,
                ShippingFee = shop.ShippingFee, Total = subtotal + shop.ShippingFee, ShippingMethodName = shop.ShippingMethodName!.Trim(),
                ShippingRateId = shop.ShippingRateId, CreatedOnUtc = now, UpdatedOnUtc = now, Lines = lines
            });
        }
        order.Subtotal = order.ShopOrders.Sum(s => s.Subtotal);
        order.ShippingTotal = order.ShopOrders.Sum(s => s.ShippingFee);
        order.Total = order.Subtotal + order.ShippingTotal;

        var (saved, created) = await store.InsertAsync(order, cancellationToken);
        if (!created)
        {
            // The same key again: the same order, unless the request is not the one that made it.
            var same = saved.CustomerId == order.CustomerId && saved.Total == order.Total && saved.PaymentMethod == order.PaymentMethod
                && saved.ShopOrders.Select(s => s.VendorId).Order().SequenceEqual(order.ShopOrders.Select(s => s.VendorId).Order());
            return same ? CatalogResult.Success(saved) : CatalogResult.Error<Order>(OrderErrors.PlacementConflict);
        }

        await auditLog.WriteAsync("order.created", command.CustomerId, entityType: "Order", entityId: saved.Id,
            details: new { orderId = saved.Id, saved.Number, saved.Total, saved.CurrencyCode, shops = saved.ShopOrders.Count }, cancellationToken: cancellationToken);
        return CatalogResult.Success(saved);
    }

    // ---- Customers ----

    public Task<PagedResult<Order>> GetMyOrdersAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken) =>
        store.GetOrdersAsync(Clamp(new OrderListQuery(customerId, null, null, null, null, null, page, pageSize)), cancellationToken);

    public async Task<CatalogResult<OrderDetail>> GetMyOrderAsync(int customerId, int orderId, CancellationToken cancellationToken)
    {
        var order = await store.GetOrderAsync(orderId, cancellationToken);
        // Someone else's order is not found, so ids reveal nothing.
        return order is null || order.CustomerId != customerId
            ? CatalogResult.Error<OrderDetail>(CatalogErrors.NotFound)
            : CatalogResult.Success(await DetailAsync(order, cancellationToken));
    }

    public async Task<CatalogResult<ShopOrderDetail>> CancelAsCustomerAsync(int customerId, int shopOrderId, string? reason, CancellationToken cancellationToken)
    {
        var shopOrder = await store.GetShopOrderAsync(shopOrderId, cancellationToken);
        if (shopOrder?.Order is null || shopOrder.Order.CustomerId != customerId) return CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound);
        return await CancelAsync(shopOrder, OrderActor.Customer, customerId, reason, cancellationToken);
    }

    public async Task<CatalogResult<ShopOrderDetail>> ConfirmReceiptAsync(int customerId, int shopOrderId, CancellationToken cancellationToken)
    {
        var shopOrder = await store.GetShopOrderAsync(shopOrderId, cancellationToken);
        if (shopOrder?.Order is null || shopOrder.Order.CustomerId != customerId) return CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound);
        return await MoveAsync(shopOrder, OrderAction.Deliver, OrderActor.Customer, customerId, null, null, null, cancellationToken);
    }

    // ---- Shop members ----

    public Task<PagedResult<ShopOrder>> GetShopOrdersAsync(int vendorId, OrderListQuery query, CancellationToken cancellationToken) =>
        // The shop id comes from the route and replaces whatever the query says.
        store.GetShopOrdersAsync(Clamp(query with { VendorId = vendorId, CustomerId = null }), cancellationToken);

    public Task<IReadOnlyDictionary<ShopOrderStatus, int>> GetCountsAsync(int vendorId, CancellationToken cancellationToken) =>
        store.CountByStatusAsync(vendorId, cancellationToken);

    public async Task<CatalogResult<ShopOrderDetail>> GetShopOrderAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken)
    {
        var shopOrder = await store.GetShopOrderAsync(shopOrderId, cancellationToken);
        return shopOrder is null || shopOrder.VendorId != vendorId
            ? CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound)
            : CatalogResult.Success(await DetailAsync(shopOrder, cancellationToken));
    }

    public async Task<CatalogResult<ShopOrderDetail>> ConfirmAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await OwnedAsync(vendorId, shopOrderId, cancellationToken);
        return shopOrder is null
            ? CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound)
            : await MoveAsync(shopOrder, OrderAction.Confirm, OrderActor.Shop, actorCustomerId, null, null, null, cancellationToken);
    }

    public async Task<CatalogResult<ShopOrderDetail>> ShipAsync(int vendorId, int shopOrderId, ShipCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await OwnedAsync(vendorId, shopOrderId, cancellationToken);
        if (shopOrder is null) return CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound);

        var errors = ValidateShipping(command, out var carrier, out var tracking);
        if (errors.Count > 0) return CatalogResult.Failure<ShopOrderDetail>(errors);
        return await MoveAsync(shopOrder, OrderAction.Ship, OrderActor.Shop, actorCustomerId, $"{carrier} {tracking}", carrier, tracking, cancellationToken);
    }

    public async Task<CatalogResult<ShopOrderDetail>> UpdateTrackingAsync(
        int vendorId, int shopOrderId, ShipCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await OwnedAsync(vendorId, shopOrderId, cancellationToken);
        if (shopOrder is null) return CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound);

        var errors = ValidateShipping(command, out var carrier, out var tracking);
        if (errors.Count > 0) return CatalogResult.Failure<ShopOrderDetail>(errors);
        if (shopOrder.Status != ShopOrderStatus.Shipped
            || !await store.TryUpdateTrackingAsync(shopOrder.Id, carrier, tracking, actorCustomerId, clock.UtcNow, cancellationToken))
            return CatalogResult.Error<ShopOrderDetail>(OrderErrors.InvalidTransition);

        return CatalogResult.Success(await DetailAsync((await store.GetShopOrderAsync(shopOrder.Id, cancellationToken))!, cancellationToken));
    }

    public async Task<CatalogResult<ShopOrderDetail>> DeliverAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await OwnedAsync(vendorId, shopOrderId, cancellationToken);
        return shopOrder is null
            ? CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound)
            : await MoveAsync(shopOrder, OrderAction.Deliver, OrderActor.Shop, actorCustomerId, null, null, null, cancellationToken);
    }

    public async Task<CatalogResult<ShopOrderDetail>> CancelAsShopAsync(int vendorId, int shopOrderId, string? reason, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await OwnedAsync(vendorId, shopOrderId, cancellationToken);
        return shopOrder is null
            ? CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound)
            : await CancelAsync(shopOrder, OrderActor.Shop, actorCustomerId, reason, cancellationToken);
    }

    // ---- Administrators ----

    public Task<PagedResult<Order>> GetOrdersAsync(OrderListQuery query, CancellationToken cancellationToken) =>
        store.GetOrdersAsync(Clamp(query with { CustomerId = null }), cancellationToken);

    public async Task<CatalogResult<OrderDetail>> GetOrderAsync(int orderId, CancellationToken cancellationToken)
    {
        var order = await store.GetOrderAsync(orderId, cancellationToken);
        return order is null ? CatalogResult.Error<OrderDetail>(CatalogErrors.NotFound) : CatalogResult.Success(await DetailAsync(order, cancellationToken));
    }

    public async Task<CatalogResult<ShopOrderDetail>> CancelAsAdminAsync(int shopOrderId, string? reason, int actorCustomerId, CancellationToken cancellationToken)
    {
        var shopOrder = await store.GetShopOrderAsync(shopOrderId, cancellationToken);
        return shopOrder is null
            ? CatalogResult.Error<ShopOrderDetail>(CatalogErrors.NotFound)
            : await CancelAsync(shopOrder, OrderActor.Admin, actorCustomerId, reason, cancellationToken);
    }

    // ---- Helpers ----

    private async Task<ShopOrder?> OwnedAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken)
    {
        var shopOrder = await store.GetShopOrderAsync(shopOrderId, cancellationToken);
        return shopOrder is not null && shopOrder.VendorId == vendorId ? shopOrder : null;
    }

    private async Task<CatalogResult<ShopOrderDetail>> CancelAsync(
        ShopOrder shopOrder, OrderActor actor, int actorCustomerId, string? reason, CancellationToken cancellationToken)
    {
        var text = reason?.Trim() ?? string.Empty;
        if (text.Length is 0 or > OrderLimits.MaxReasonLength)
            return CatalogResult.Failure<ShopOrderDetail>("reason", $"Give a reason of 1 to {OrderLimits.MaxReasonLength} characters.");
        return await MoveAsync(shopOrder, OrderAction.Cancel, actor, actorCustomerId, text, null, null, cancellationToken, cancelReason: text);
    }

    /// <summary>Applies an action through the rules and the compare-and-set. A lost race is an invalid transition now.</summary>
    private async Task<CatalogResult<ShopOrderDetail>> MoveAsync(
        ShopOrder shopOrder, OrderAction action, OrderActor actor, int? actorCustomerId, string? note, string? carrier, string? tracking,
        CancellationToken cancellationToken, string? cancelReason = null)
    {
        if (OrderRules.Transition(shopOrder.Status, action, actor) is not { } target) return CatalogResult.Error<ShopOrderDetail>(OrderErrors.InvalidTransition);

        var changed = await store.TryTransitionAsync(
            new ShopOrderTransition(shopOrder.Id, shopOrder.Status, target, actor, actorCustomerId, note, carrier, tracking, cancelReason, clock.UtcNow), cancellationToken);
        if (!changed) return CatalogResult.Error<ShopOrderDetail>(OrderErrors.InvalidTransition);

        await auditLog.WriteAsync("order.shop_order_changed", actorCustomerId, entityType: "ShopOrder", entityId: shopOrder.Id,
            details: new { shopOrderId = shopOrder.Id, shopOrder.OrderId, from = OrderRules.ToWire(shopOrder.Status), to = OrderRules.ToWire(target), actor = OrderRules.ToWire(actor) },
            cancellationToken: cancellationToken);
        return CatalogResult.Success(await DetailAsync((await store.GetShopOrderAsync(shopOrder.Id, cancellationToken))!, cancellationToken));
    }

    private async Task<ShopOrderDetail> DetailAsync(ShopOrder shopOrder, CancellationToken cancellationToken) =>
        new(shopOrder, (await store.GetHistoryAsync([shopOrder.Id], cancellationToken)).ToList());

    private async Task<OrderDetail> DetailAsync(Order order, CancellationToken cancellationToken)
    {
        var history = await store.GetHistoryAsync(order.ShopOrders.Select(s => s.Id).ToList(), cancellationToken);
        return new OrderDetail(order, history.GroupBy(h => h.ShopOrderId).ToDictionary(g => g.Key, g => (IReadOnlyList<OrderHistoryEntry>)g.ToList()));
    }

    private static OrderListQuery Clamp(OrderListQuery query) =>
        query with { Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, 100), Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim() };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string[]> ValidateShipping(ShipCommand command, out string carrier, out string tracking)
    {
        var errors = new Dictionary<string, string[]>();
        carrier = command.Carrier?.Trim() ?? string.Empty;
        tracking = command.TrackingNumber?.Trim() ?? string.Empty;
        if (carrier.Length is 0 or > OrderLimits.MaxCarrierLength) errors["carrier"] = [$"Enter the carrier (1 to {OrderLimits.MaxCarrierLength} characters)."];
        if (tracking.Length is 0 or > OrderLimits.MaxTrackingLength) errors["trackingNumber"] = [$"Enter the tracking number (1 to {OrderLimits.MaxTrackingLength} characters)."];
        return errors;
    }

    private static void ValidateRecipient(NewOrderRecipient? recipient, Dictionary<string, string[]> errors)
    {
        if (recipient is null)
        {
            errors["recipientName"] = ["The order needs a recipient."];
            return;
        }

        static bool Bad(string? value, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max;
        if (Bad(recipient.Name, 200)) errors["recipientName"] = ["Enter the recipient's name (at most 200 characters)."];
        if (Bad(recipient.Phone, 50)) errors["recipientPhone"] = ["Enter a phone number (at most 50 characters)."];
        if (Bad(recipient.Address1, 200)) errors["address1"] = ["Enter the address (at most 200 characters)."];
        if (Bad(recipient.City, 100)) errors["city"] = ["Enter the city (at most 100 characters)."];
        var country = recipient.CountryCode?.Trim() ?? string.Empty;
        if (country.Length != 2 || !country.All(char.IsAsciiLetter)) errors["countryCode"] = ["Choose a country."];
        if (recipient.Address2?.Trim().Length > 200) errors["address2"] = ["The address line can have at most 200 characters."];
        if (recipient.StateProvince?.Trim().Length > 100) errors["stateProvince"] = ["The state can have at most 100 characters."];
        if (recipient.PostalCode?.Trim().Length > 20) errors["postalCode"] = ["The postal code can have at most 20 characters."];
    }

    private static void ValidateShop(NewShopOrder shop, int places, Dictionary<string, string[]> errors)
    {
        var method = shop.ShippingMethodName?.Trim() ?? string.Empty;
        if (method.Length is 0 or > 100) errors["shippingMethodName"] = ["Every shop needs a shipping method name (at most 100 characters)."];
        if (shop.ShippingFee is < 0 or > OrderLimits.MaxAmount || !CurrencyRules.HasValidScale(shop.ShippingFee, places))
            errors["shippingFee"] = [$"The shipping fee must be 0 or more, with at most {places} decimal places."];

        var lines = shop.Lines ?? [];
        if (lines.Count is 0 or > OrderLimits.MaxLinesPerShop)
        {
            errors["lines"] = [$"A shop needs 1 to {OrderLimits.MaxLinesPerShop} lines."];
            return;
        }

        foreach (var line in lines)
        {
            if (line.ProductId <= 0 || string.IsNullOrWhiteSpace(line.Name) || line.Name.Trim().Length > 200) errors["lines"] = ["Every line needs a product and a name (at most 200 characters)."];
            if (line.Quantity is < 1 or > OrderLimits.MaxQuantity) errors["quantity"] = [$"The quantity must be between 1 and {OrderLimits.MaxQuantity}."];
            if (line.UnitPrice is < 0 or > OrderLimits.MaxAmount || !CurrencyRules.HasValidScale(line.UnitPrice, places))
                errors["unitPrice"] = [$"The unit price must be 0 or more, with at most {places} decimal places."];
            if (line.VariantLabel?.Trim().Length > 200 || line.Sku?.Trim().Length > 100) errors["lines"] = ["The variant label or SKU is too long."];
        }
    }
}
