using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Orders;

/// <summary>
/// Turns selected cart lines into an order (F17-A). The cart prices and checks every line; checkout adds shipping, the order limits,
/// the own-shop rule and the delivery address, then hands one atomic write to <see cref="IOrderStore.PlaceAsync"/>.
/// </summary>
public sealed class CheckoutService(
    ICartService cartService,
    IOrderStore orderStore,
    ICustomerAccountDataStore addressStore,
    ICustomerIdentityStore identityStore,
    IVendorMemberStore memberStore,
    IAuditLogService auditLog,
    OrderNotifier notifier,
    IClock clock,
    IOptions<OrderOptions> options) : ICheckoutService
{
    public async Task<OrderResult<CheckoutPreview>> PreviewAsync(int customerId, IReadOnlyList<int> cartItemIds, CancellationToken cancellationToken)
    {
        var invalid = ValidateSelection(cartItemIds);
        if (invalid is not null) return OrderResult.Failure<CheckoutPreview>(invalid);
        return OrderResult.Success(await BuildPreviewAsync(customerId, cartItemIds, cancellationToken));
    }

    public async Task<OrderResult<PlacedOrder>> PlaceAsync(int customerId, PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        var errors = ValidateSelection(command.CartItemIds) ?? new Dictionary<string, string[]>();
        if (command.PaymentMethod is not PaymentMethod.CashOnDelivery) errors["paymentMethod"] = ["Choose cash on delivery."];
        var key = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (key.Length is 0 or > OrderLimits.MaxIdempotencyKeyLength)
            errors["idempotencyKey"] = [$"Send an Idempotency-Key header of 1 to {OrderLimits.MaxIdempotencyKeyLength} characters."];
        foreach (var (vendorId, note) in command.Notes ?? new Dictionary<int, string?>())
        {
            if (note?.Trim().Length > OrderLimits.MaxNoteLength)
                errors[$"notes.{vendorId}"] = [$"A note to a shop can have at most {OrderLimits.MaxNoteLength} characters."];
        }
        if (errors.Count > 0) return OrderResult.Failure<PlacedOrder>(errors);

        // A retried request returns the order the first one created.
        if (await orderStore.FindIdByIdempotencyKeyAsync(customerId, key, cancellationToken) is { } previousId)
            return await PlacedAsync(previousId, created: false, cancellationToken);

        var address = await addressStore.GetAddressAsync(customerId, command.AddressId, cancellationToken);
        if (address is null) return OrderResult.Error<PlacedOrder>(OrderErrors.AddressInvalid);

        var preview = await BuildPreviewAsync(customerId, command.CartItemIds, cancellationToken);
        if (preview.MissingCartItemIds.Count > 0) return OrderResult.Error<PlacedOrder>(OrderErrors.CartItemNotFound);
        if (preview.Groups.Count > OrderLimits.MaxShops)
            return OrderResult.Failure<PlacedOrder>("cartItemIds", $"An order can include at most {OrderLimits.MaxShops} shops.");
        if (!preview.CanPlace) return OrderResult.Error<PlacedOrder>(OrderErrors.ItemsUnavailable);
        // Prices or fees moved while the customer was on the checkout page: they confirm the new total instead of paying it unseen.
        if (preview.Total != command.ExpectedTotal) return OrderResult.Error<PlacedOrder>(OrderErrors.TotalChanged);

        var order = BuildOrder(customerId, address, preview, command.Notes, key);
        var stored = await orderStore.PlaceAsync(order, command.CartItemIds.Distinct().ToList(), cancellationToken);
        switch (stored.Outcome)
        {
            case PlaceOrderOutcome.Duplicate:
                return await PlacedAsync(stored.OrderId, created: false, cancellationToken);
            case PlaceOrderOutcome.InsufficientStock:
                return OrderResult.Error<PlacedOrder>(OrderErrors.ItemsUnavailable);
            case PlaceOrderOutcome.CartItemMissing:
                return OrderResult.Error<PlacedOrder>(OrderErrors.CartItemNotFound);
        }

        // Ids and numbers only: the address and phone are personal data and stay out of the audit log.
        await auditLog.WriteAsync("order.placed", customerId, entityType: "CustomerOrder", entityId: order.Id,
            details: new { orderNumber = order.OrderNumber, storeOrders = order.StoreOrders.Select(s => s.SubOrderNumber).ToArray(), total = order.Total, currency = order.CurrencyCode },
            cancellationToken: cancellationToken);

        var placed = (await orderStore.GetAsync(order.Id, cancellationToken))!;
        var customer = await identityStore.FindByIdAsync(customerId, cancellationToken);
        if (customer is not null) await notifier.OrderPlacedAsync(placed, customer.Email, cancellationToken);
        return OrderResult.Success(new PlacedOrder(placed, Created: true));
    }

    // ---- Helpers ----

    private static Dictionary<string, string[]>? ValidateSelection(IReadOnlyList<int>? cartItemIds)
    {
        var count = cartItemIds?.Distinct().Count() ?? 0;
        if (count == 0) return new() { ["cartItemIds"] = ["Choose at least one item to buy."] };
        if (count > OrderLimits.MaxLines) return new() { ["cartItemIds"] = [$"An order can include at most {OrderLimits.MaxLines} items."] };
        return null;
    }

    private async Task<CheckoutPreview> BuildPreviewAsync(int customerId, IReadOnlyList<int> cartItemIds, CancellationToken cancellationToken)
    {
        var cart = await cartService.GetAsync(customerId, cancellationToken);
        var wanted = cartItemIds.ToHashSet();
        var lines = cart.Groups.SelectMany(g => g.Lines).Where(l => wanted.Contains(l.Id)).ToList();
        var missing = wanted.Except(lines.Select(l => l.Id)).Order().ToList();

        // Membership can change after a line was added; buying from your own shop is refused at checkout too.
        var ownShops = new HashSet<int>();
        foreach (var vendorId in lines.Select(l => l.VendorId).Distinct())
        {
            if (await memberStore.GetAsync(vendorId, customerId, cancellationToken) is not null) ownShops.Add(vendorId);
        }

        var settings = options.Value;
        var groups = lines.GroupBy(l => l.VendorId)
            .Select(g =>
            {
                var itemsTotal = g.Sum(l => l.LineTotal);
                var fee = OrderRules.ShippingFee(itemsTotal, settings);
                return new CheckoutShopGroup(g.Key, g.First().VendorName, g.ToList(), itemsTotal, fee, itemsTotal + fee);
            })
            .ToList();

        var canPlace = lines.Count > 0 && missing.Count == 0 && ownShops.Count == 0 && groups.Count <= OrderLimits.MaxShops
            && lines.All(l => !l.Issues.Any(CartRules.IsBlocking));
        var itemsTotal = groups.Sum(g => g.ItemsTotal);
        var shippingTotal = groups.Sum(g => g.ShippingFee);
        return new CheckoutPreview(cart.CurrencyCode, groups, itemsTotal, shippingTotal, itemsTotal + shippingTotal, canPlace, missing,
            lines.Where(l => ownShops.Contains(l.VendorId)).Select(l => l.Id).ToList());
    }

    private CustomerOrder BuildOrder(int customerId, CustomerAddress address, CheckoutPreview preview, IReadOnlyDictionary<int, string?>? notes, string key)
    {
        var now = clock.UtcNow;
        var confirmBy = now.AddHours(options.Value.ConfirmWithinHours);
        return new CustomerOrder
        {
            CustomerId = customerId,
            CurrencyCode = preview.CurrencyCode,
            ItemsTotal = preview.ItemsTotal,
            ShippingTotal = preview.ShippingTotal,
            Total = preview.Total,
            PaymentMethod = PaymentMethod.CashOnDelivery,
            IdempotencyKey = key,
            CreatedOnUtc = now,
            ShippingAddress = new OrderAddress
            {
                FirstName = address.FirstName, LastName = address.LastName, Company = address.Company, Address1 = address.Address1,
                Address2 = address.Address2, City = address.City, StateProvince = address.StateProvince, CountryCode = address.CountryCode,
                ZipPostalCode = address.ZipPostalCode, PhoneNumber = address.PhoneNumber
            },
            StoreOrders = preview.Groups.Select(g => new StoreOrder
            {
                VendorId = g.VendorId,
                VendorName = g.VendorName,
                Status = StoreOrderStatus.Pending,
                PaymentStatus = PaymentStatus.Pending,
                ItemsTotal = g.ItemsTotal,
                ShippingFee = g.ShippingFee,
                Total = g.Total,
                CustomerNote = notes is not null && notes.TryGetValue(g.VendorId, out var note) && !string.IsNullOrWhiteSpace(note) ? note.Trim() : null,
                ConfirmByUtc = confirmBy,
                CreatedOnUtc = now,
                UpdatedOnUtc = now,
                Items = g.Lines.Select(l => new OrderItem
                {
                    ProductId = l.ProductId, CombinationId = l.CombinationId, ValueIds = l.ValueKey, ProductName = l.Name,
                    VariantDescription = l.VariantLabel, Sku = l.Sku, PictureId = l.MainPictureId, UnitPrice = l.UnitPrice,
                    Quantity = l.Quantity, LineTotal = l.LineTotal,
                    // The cart reports an available quantity only for products that track stock.
                    StockDeducted = l.AvailableQuantity is not null
                }).ToList(),
                Events = [new StoreOrderEvent { ToStatus = StoreOrderStatus.Pending, ActorType = OrderActorType.Customer, ActorCustomerId = customerId, CreatedOnUtc = now }]
            }).ToList()
        };
    }

    private async Task<OrderResult<PlacedOrder>> PlacedAsync(int orderId, bool created, CancellationToken cancellationToken)
    {
        var order = await orderStore.GetAsync(orderId, cancellationToken);
        return order is null ? OrderResult.Error<PlacedOrder>(OrderErrors.NotFound) : OrderResult.Success(new PlacedOrder(order, created));
    }
}
