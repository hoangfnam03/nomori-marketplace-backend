using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Domain.Customers;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Services.Orders;

namespace Nomori.Marketplace.Services.Tests;

public sealed class OrderTests
{
    private const int Buyer = 7;
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ---- Rules ----

    [Fact]
    public void OverallStatusFollowsTheShopOrders()
    {
        Assert.Equal(OrderOverallStatus.Processing, OrderRules.OverallStatus([StoreOrderStatus.Pending, StoreOrderStatus.Delivered]));
        Assert.Equal(OrderOverallStatus.Delivered, OrderRules.OverallStatus([StoreOrderStatus.Completed, StoreOrderStatus.Cancelled]));
        Assert.Equal(OrderOverallStatus.Cancelled, OrderRules.OverallStatus([StoreOrderStatus.Cancelled, StoreOrderStatus.Cancelled]));
    }

    [Fact]
    public void ShippingIsAFlatFeeWithAFreeThreshold()
    {
        var options = new OrderOptions { ShippingFeePerStoreOrder = 2m, FreeShippingThreshold = 50m };
        Assert.Equal(2m, OrderRules.ShippingFee(49.99m, options));
        Assert.Equal(0m, OrderRules.ShippingFee(50m, options));
        Assert.Equal(2m, OrderRules.ShippingFee(1000m, new OrderOptions { ShippingFeePerStoreOrder = 2m }));
    }

    [Fact]
    public void NumbersAndCustomerTransitions()
    {
        Assert.Equal("NM261004-0007", OrderRules.OrderNumber(new DateTime(2026, 10, 4), 7));
        Assert.Equal("NM261004-0007-2", OrderRules.SubOrderNumber("NM261004-0007", 2));

        Assert.True(OrderRules.CustomerMay(StoreOrderStatus.Pending, StoreOrderStatus.Cancelled));
        Assert.True(OrderRules.CustomerMay(StoreOrderStatus.Shipped, StoreOrderStatus.Delivered));
        Assert.False(OrderRules.CustomerMay(StoreOrderStatus.Confirmed, StoreOrderStatus.Cancelled));
        Assert.False(OrderRules.CustomerMay(StoreOrderStatus.Pending, StoreOrderStatus.Confirmed));
        Assert.False(OrderRules.CustomerMay(StoreOrderStatus.Pending, StoreOrderStatus.Delivered));
    }

    // ---- Checkout ----

    [Fact]
    public async Task PreviewGroupsByShopWithShipping()
    {
        var f = new Fixture { Options = new OrderOptions { ShippingFeePerStoreOrder = 3m, FreeShippingThreshold = 40m } };
        f.Cart.Add(Line(1, vendorId: 10, price: 25m, quantity: 2)); // 50: free shipping
        f.Cart.Add(Line(2, vendorId: 20, price: 5m));
        f.Cart.Add(Line(3, vendorId: 20, price: 7m));
        f.Cart.Add(Line(4, vendorId: 30, price: 9m)); // not selected

        var preview = (await f.Checkout().PreviewAsync(Buyer, [1, 2, 3], CancellationToken.None)).Value!;

        Assert.Equal([10, 20], preview.Groups.Select(g => g.VendorId));
        Assert.Equal((50m, 0m), (preview.Groups[0].ItemsTotal, preview.Groups[0].ShippingFee));
        Assert.Equal((12m, 3m, 15m), (preview.Groups[1].ItemsTotal, preview.Groups[1].ShippingFee, preview.Groups[1].Total));
        Assert.Equal((62m, 3m, 65m), (preview.ItemsTotal, preview.ShippingTotal, preview.Total));
        Assert.True(preview.CanPlace);
    }

    [Fact]
    public async Task PreviewCannotPlaceMissingBlockedOrOwnShopLines()
    {
        var f = new Fixture();
        f.Cart.Add(Line(1, vendorId: 10));
        f.Cart.Add(Line(2, vendorId: 20, issues: [CartIssues.OutOfStock]));
        f.Cart.Add(Line(3, vendorId: 30, issues: [CartIssues.PriceChanged]));
        f.Members.Members.Add(new VendorMember { CustomerId = Buyer });

        var missing = (await f.Checkout().PreviewAsync(Buyer, [1, 99], CancellationToken.None)).Value!;
        Assert.Equal([99], missing.MissingCartItemIds);
        Assert.False(missing.CanPlace);

        // NoMembers answers for any shop, so every selected line belongs to "your own shop" here.
        var own = (await f.Checkout().PreviewAsync(Buyer, [1], CancellationToken.None)).Value!;
        Assert.Equal([1], own.OwnShopCartItemIds);
        Assert.False(own.CanPlace);

        f.Members.Members.Clear();
        Assert.False((await f.Checkout().PreviewAsync(Buyer, [2], CancellationToken.None)).Value!.CanPlace);
        // A price change is a notice, not a blocker.
        Assert.True((await f.Checkout().PreviewAsync(Buyer, [3], CancellationToken.None)).Value!.CanPlace);

        Assert.Contains("cartItemIds", (await f.Checkout().PreviewAsync(Buyer, [], CancellationToken.None)).Errors.Keys);
        Assert.Contains("cartItemIds", (await f.Checkout().PreviewAsync(Buyer, Enumerable.Range(1, 51).ToArray(), CancellationToken.None)).Errors.Keys);
    }

    [Fact]
    public async Task PlaceCreatesOneShopOrderPerShopAndCopiesWhatWasBought()
    {
        var f = new Fixture { EmailEnabled = true, Options = new OrderOptions { ShippingFeePerStoreOrder = 1m } };
        f.Cart.Add(Line(1, vendorId: 10, price: 4m, quantity: 2, tracked: true, combinationId: 55, valueKey: "3,8", variant: "Red / S"));
        f.Cart.Add(Line(2, vendorId: 20, price: 6m, tracked: false));
        f.Members.Members.Add(new VendorMember { CustomerId = 99, Email = "shop@test" });

        var result = await f.Checkout().PlaceAsync(Buyer, Command([1, 2], expectedTotal: 16m, notes: new() { [10] = "  ring the bell  " }), CancellationToken.None);

        Assert.True(result.Succeeded, string.Join(";", result.Errors.SelectMany(e => e.Value)) + result.ErrorCode);
        Assert.True(result.Value!.Created);
        var order = f.Orders.Placed.Single();
        Assert.Equal(("USD", 14m, 2m, 16m), (order.CurrencyCode, order.ItemsTotal, order.ShippingTotal, order.Total));
        Assert.Equal(("An", "Hanoi", "+84 900"), (order.ShippingAddress.FirstName, order.ShippingAddress.City, order.ShippingAddress.PhoneNumber));
        Assert.Equal([10, 20], order.StoreOrders.Select(s => s.VendorId));

        var first = order.StoreOrders[0];
        Assert.Equal((StoreOrderStatus.Pending, PaymentStatus.Pending), (first.Status, first.PaymentStatus));
        Assert.Equal("ring the bell", first.CustomerNote);
        Assert.Null(order.StoreOrders[1].CustomerNote);
        Assert.Equal(Now.AddHours(48), first.ConfirmByUtc);
        var item = first.Items.Single();
        Assert.Equal((55, "3,8", "Red / S", 4m, 2, 8m, true), (item.CombinationId, item.ValueIds, item.VariantDescription, item.UnitPrice, item.Quantity, item.LineTotal, item.StockDeducted));
        Assert.False(order.StoreOrders[1].Items.Single().StockDeducted);
        Assert.Equal((StoreOrderStatus.Pending, OrderActorType.Customer, (int?)Buyer),
            (first.Events.Single().ToStatus, first.Events.Single().ActorType, first.Events.Single().ActorCustomerId));
        Assert.Equal([1, 2], f.Orders.LastCartItemIds);

        Assert.Contains("order.placed", f.Audit.Events);
        Assert.Contains(f.Email.Sent, m => m.ToAddress == "buyer@test" && m.Subject.Contains(order.OrderNumber, StringComparison.Ordinal));
        Assert.Contains(f.Email.Sent, m => m.ToAddress == "shop@test");
    }

    [Fact]
    public async Task PlaceRefusesWhatTheCustomerHasNotSeenOrCannotBuy()
    {
        var f = new Fixture();
        f.Cart.Add(Line(1, vendorId: 10, price: 5m));
        f.Cart.Add(Line(2, vendorId: 20, issues: [CartIssues.Unavailable]));
        var checkout = f.Checkout();

        Assert.Equal(OrderErrors.TotalChanged, (await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 4m), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.ItemsUnavailable, (await checkout.PlaceAsync(Buyer, Command([1, 2], expectedTotal: 5m), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.CartItemNotFound, (await checkout.PlaceAsync(Buyer, Command([1, 3], expectedTotal: 5m), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.AddressInvalid, (await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 5m) with { AddressId = 999 }, CancellationToken.None)).ErrorCode);
        Assert.Contains("paymentMethod", (await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 5m) with { PaymentMethod = null }, CancellationToken.None)).Errors.Keys);
        Assert.Contains("idempotencyKey", (await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 5m) with { IdempotencyKey = " " }, CancellationToken.None)).Errors.Keys);
        Assert.Contains("notes.10", (await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 5m, notes: new() { [10] = new string('x', 501) }), CancellationToken.None)).Errors.Keys);
        Assert.Empty(f.Orders.Placed);
    }

    [Fact]
    public async Task PlaceAllowsAtMostTenShops()
    {
        var f = new Fixture();
        for (var i = 1; i <= 11; i++) f.Cart.Add(Line(i, vendorId: i * 10, price: 1m));

        var result = await f.Checkout().PlaceAsync(Buyer, Command(Enumerable.Range(1, 11).ToArray(), expectedTotal: 11m), CancellationToken.None);

        Assert.Contains("cartItemIds", result.Errors.Keys);
    }

    [Fact]
    public async Task ARetriedRequestReturnsTheFirstOrder()
    {
        var f = new Fixture();
        f.Cart.Add(Line(1, vendorId: 10, price: 5m));
        var checkout = f.Checkout();

        var first = await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 5m), CancellationToken.None);
        // The cart line is gone after the first order; the retry must still succeed with the same order.
        f.Cart.Lines.Clear();
        var retry = await checkout.PlaceAsync(Buyer, Command([1], expectedTotal: 5m), CancellationToken.None);

        Assert.True(first.Value!.Created);
        Assert.False(retry.Value!.Created);
        Assert.Equal(first.Value.Order.Id, retry.Value.Order.Id);
        Assert.Single(f.Orders.Placed);
    }

    [Fact]
    public async Task StockRunningOutAtTheLastMomentPlacesNothing()
    {
        var f = new Fixture();
        f.Cart.Add(Line(1, vendorId: 10, price: 5m, tracked: true));
        f.Orders.NextOutcome = PlaceOrderOutcome.InsufficientStock;

        Assert.Equal(OrderErrors.ItemsUnavailable, (await f.Checkout().PlaceAsync(Buyer, Command([1], expectedTotal: 5m), CancellationToken.None)).ErrorCode);

        f.Orders.NextOutcome = PlaceOrderOutcome.CartItemMissing;
        Assert.Equal(OrderErrors.CartItemNotFound, (await f.Checkout().PlaceAsync(Buyer, Command([1], expectedTotal: 5m) with { IdempotencyKey = "other" }, CancellationToken.None)).ErrorCode);
        Assert.DoesNotContain("order.placed", f.Audit.Events);
    }

    // ---- My orders ----

    [Fact]
    public async Task CustomersOnlySeeTheirOwnOrders()
    {
        var f = new Fixture();
        var order = f.Orders.Seed(Buyer, StoreOrderStatus.Pending);

        Assert.True((await f.Service().GetForCustomerAsync(Buyer, order.Id, CancellationToken.None)).Succeeded);
        Assert.Equal(OrderErrors.NotFound, (await f.Service().GetForCustomerAsync(8, order.Id, CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.NotFound, (await f.Service().ChangeStatusAsync(order.StoreOrders[0].Id, Cancel(), AsCustomer(8), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.NotFound, (await f.Service().ReorderAsync(8, order.StoreOrders[0].Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task CancellingAPendingShopOrderNeedsAReasonAndVoidsPayment()
    {
        var f = new Fixture { EmailEnabled = true };
        f.Members.Members.Add(new VendorMember { CustomerId = 99, Email = "shop@test" });
        var storeOrder = f.Orders.Seed(Buyer, StoreOrderStatus.Pending).StoreOrders[0];
        var service = f.Service();

        Assert.Contains("reason", (await service.ChangeStatusAsync(storeOrder.Id, Cancel(reason: null), AsCustomer(Buyer), CancellationToken.None)).Errors.Keys);
        Assert.Contains("reason", (await service.ChangeStatusAsync(storeOrder.Id, Cancel(reason: "bored"), AsCustomer(Buyer), CancellationToken.None)).Errors.Keys);
        Assert.Contains("note", (await service.ChangeStatusAsync(storeOrder.Id, Cancel(reason: CustomerCancelReasons.Other), AsCustomer(Buyer), CancellationToken.None)).Errors.Keys);
        Assert.Contains("status", (await service.ChangeStatusAsync(storeOrder.Id, new ChangeStoreOrderStatusCommand(null, null, null), AsCustomer(Buyer), CancellationToken.None)).Errors.Keys);
        Assert.Empty(f.Orders.Transitions);

        var result = await service.ChangeStatusAsync(storeOrder.Id, Cancel(CustomerCancelReasons.ChangedMind, " not needed "), AsCustomer(Buyer), CancellationToken.None);

        Assert.True(result.Succeeded);
        var t = f.Orders.Transitions.Single();
        Assert.Equal((StoreOrderStatus.Pending, StoreOrderStatus.Cancelled, OrderActorType.Customer, (PaymentStatus?)PaymentStatus.Voided, "changed_mind", "not needed"),
            (t.From, t.To, t.ActorType, t.NewPaymentStatus, t.Reason, t.Note));
        Assert.Equal(StoreOrderStatus.Cancelled, result.Value!.StoreOrders[0].Status);
        Assert.Contains("order.cancelled_by_customer", f.Audit.Events);
        Assert.Contains(f.Email.Sent, m => m.ToAddress == "shop@test");
    }

    [Fact]
    public async Task CustomersCannotCancelOnceTheShopConfirmed()
    {
        var f = new Fixture();
        var confirmed = f.Orders.Seed(Buyer, StoreOrderStatus.Confirmed).StoreOrders[0];
        Assert.Equal(OrderErrors.InvalidTransition, (await f.Service().ChangeStatusAsync(confirmed.Id, Cancel(), AsCustomer(Buyer), CancellationToken.None)).ErrorCode);

        // The shop confirmed a moment before the customer's click reached the database.
        var racing = f.Orders.Seed(Buyer, StoreOrderStatus.Pending).StoreOrders[0];
        f.Orders.RejectNextTransition = true;
        Assert.Equal(OrderErrors.ConcurrentUpdate, (await f.Service().ChangeStatusAsync(racing.Id, Cancel(), AsCustomer(Buyer), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task ConfirmingReceiptDeliversAndMarksCashOnDeliveryPaid()
    {
        var f = new Fixture();
        var shipped = f.Orders.Seed(Buyer, StoreOrderStatus.Shipped).StoreOrders[0];

        var result = await f.Service().ChangeStatusAsync(shipped.Id, new ChangeStoreOrderStatusCommand(StoreOrderStatus.Delivered, "ignored", "ignored"), AsCustomer(Buyer), CancellationToken.None);

        Assert.True(result.Succeeded);
        var t = f.Orders.Transitions.Single();
        Assert.Equal((StoreOrderStatus.Delivered, (PaymentStatus?)PaymentStatus.Paid, (string?)null), (t.To, t.NewPaymentStatus, t.Reason));
        Assert.Contains("order.received", f.Audit.Events);
    }

    [Fact]
    public async Task ReorderAddsWhatCanStillBeBought()
    {
        var f = new Fixture();
        var storeOrder = f.Orders.Seed(Buyer, StoreOrderStatus.Delivered, [(1, "Mug", "3,8", 2), (2, "Gone", "", 1)]).StoreOrders[0];
        f.Cart.Refuse[2] = CatalogErrors.NotFound;

        var result = (await f.Service().ReorderAsync(Buyer, storeOrder.Id, CancellationToken.None)).Value!;

        Assert.Equal([1], result.AddedProductIds);
        Assert.Equal(("Gone", CatalogErrors.NotFound), (result.Failed.Single().ProductName, result.Failed.Single().Reason));
        var added = f.Cart.Added.Single();
        Assert.Equal((1, 2, "3,8"), (added.ProductId, added.Quantity, string.Join(',', added.ValueIds!)));
    }


    // ---- Shop and administrator ----

    [Fact]
    public async Task OnlyTheCustomerTheShopAndAdminsMayChangeAShopOrder()
    {
        var f = new Fixture();
        var storeOrder = f.Orders.Seed(Buyer, StoreOrderStatus.Pending).StoreOrders[0];

        Assert.Equal(OrderErrors.NotFound, (await f.Service().ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Confirmed), OtherShop, CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.NotFound, (await f.Service().ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Confirmed), AsCustomer(8), CancellationToken.None)).ErrorCode);
        // The customer cannot confirm on the shop's behalf, and an administrator only cancels.
        Assert.Equal(OrderErrors.InvalidTransition, (await f.Service().ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Confirmed), AsCustomer(Buyer), CancellationToken.None)).ErrorCode);
        Assert.Equal(OrderErrors.InvalidTransition, (await f.Service().ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Confirmed), Admin, CancellationToken.None)).ErrorCode);
        Assert.Empty(f.Orders.Transitions);
    }

    [Fact]
    public async Task TheShopConfirmsShipsAndMarksDelivered()
    {
        var f = new Fixture { EmailEnabled = true };
        var storeOrder = f.Orders.Seed(Buyer, StoreOrderStatus.Pending).StoreOrders[0];
        var service = f.Service();

        var confirmed = await service.ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Confirmed), ShopMember, CancellationToken.None);
        Assert.True(confirmed.Succeeded);
        Assert.Equal(OrderActorType.Vendor, f.Orders.Transitions[^1].ActorType);
        Assert.Contains(f.Email.Sent, m => m.ToAddress == "buyer@test" && m.Subject.Contains("confirmed", StringComparison.Ordinal));

        Assert.Contains("carrier", (await service.ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Shipped, tracking: "X1"), ShopMember, CancellationToken.None)).Errors.Keys);
        Assert.Contains("trackingNumber", (await service.ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Shipped, carrier: "GHN"), ShopMember, CancellationToken.None)).Errors.Keys);

        Assert.True((await service.ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Shipped, carrier: " GHN ", tracking: " GHN123 "), ShopMember, CancellationToken.None)).Succeeded);
        Assert.Equal(("GHN", "GHN123"), (f.Orders.Transitions[^1].Carrier, f.Orders.Transitions[^1].TrackingNumber));
        Assert.Contains(f.Email.Sent, m => m.Subject.Contains("on its way", StringComparison.Ordinal) && m.HtmlBody.Contains("GHN123", StringComparison.Ordinal));

        // Sending "shipped" again corrects the tracking details without a transition.
        Assert.True((await service.ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Shipped, carrier: "GHTK", tracking: "T9"), ShopMember, CancellationToken.None)).Succeeded);
        Assert.Equal((storeOrder.Id, "GHTK", "T9"), f.Orders.ShipmentUpdates.Single());

        Assert.Equal(OrderErrors.InvalidTransition, (await service.ChangeStatusAsync(storeOrder.Id, Cancel(VendorCancelReasons.OutOfStock), ShopMember, CancellationToken.None)).ErrorCode);
        Assert.True((await service.ChangeStatusAsync(storeOrder.Id, To(StoreOrderStatus.Delivered), ShopMember, CancellationToken.None)).Succeeded);
        Assert.Equal((PaymentStatus?)PaymentStatus.Paid, f.Orders.Transitions[^1].NewPaymentStatus);
    }

    [Fact]
    public async Task TheShopCancelsBeforeShippingWithItsOwnReasons()
    {
        var f = new Fixture();
        var storeOrder = f.Orders.Seed(Buyer, StoreOrderStatus.Confirmed).StoreOrders[0];
        var service = f.Service();

        Assert.Contains("reason", (await service.ChangeStatusAsync(storeOrder.Id, Cancel(CustomerCancelReasons.ChangedMind), ShopMember, CancellationToken.None)).Errors.Keys);
        Assert.Contains("note", (await service.ChangeStatusAsync(storeOrder.Id, Cancel(VendorCancelReasons.Other), ShopMember, CancellationToken.None)).Errors.Keys);

        var result = await service.ChangeStatusAsync(storeOrder.Id, Cancel(VendorCancelReasons.OutOfStock), ShopMember, CancellationToken.None);

        Assert.True(result.Succeeded);
        var t = f.Orders.Transitions.Single();
        Assert.Equal((true, (PaymentStatus?)PaymentStatus.Voided, "out_of_stock"), (t.Restock, t.NewPaymentStatus, t.Reason));
        Assert.Contains("order.cancelled_by_vendor", f.Audit.Events);
    }

    [Fact]
    public async Task AnAdministratorCancelsWithANoteAndShippedStockIsNotPutBack()
    {
        var f = new Fixture();
        var delivered = f.Orders.Seed(Buyer, StoreOrderStatus.Delivered, payment: PaymentStatus.Paid).StoreOrders[0];
        var service = f.Service();

        Assert.Contains("note", (await service.ChangeStatusAsync(delivered.Id, Cancel(reason: null), Admin, CancellationToken.None)).Errors.Keys);
        Assert.True((await service.ChangeStatusAsync(delivered.Id, Cancel(reason: null, note: "Fraudulent listing"), Admin, CancellationToken.None)).Succeeded);

        var t = f.Orders.Transitions.Single();
        Assert.Equal((OrderActorType.Admin, "admin", false, (PaymentStatus?)PaymentStatus.Refunded), (t.ActorType, t.Reason, t.Restock, t.NewPaymentStatus));

        var completed = f.Orders.Seed(Buyer, StoreOrderStatus.Completed).StoreOrders[0];
        Assert.Equal(OrderErrors.InvalidTransition, (await service.ChangeStatusAsync(completed.Id, Cancel(reason: null, note: "late"), Admin, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task AShopSeesOnlyItsOwnPartOfAnOrder()
    {
        var f = new Fixture();
        var order = f.Orders.Seed(Buyer, StoreOrderStatus.Pending);
        order.StoreOrders.Add(new StoreOrder { Id = 900, OrderId = order.Id, VendorId = 20, Status = StoreOrderStatus.Pending, Total = 99m });
        order.StoreOrders[0].Total = 5m;

        var seen = (await f.Service().GetForVendorAsync(10, order.StoreOrders[0].Id, CancellationToken.None)).Value!;

        Assert.Equal([10], seen.StoreOrders.Select(s => s.VendorId));
        Assert.Equal(5m, seen.Total);
        Assert.Equal(OrderErrors.NotFound, (await f.Service().GetForVendorAsync(20, order.StoreOrders[0].Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task BulkConfirmSkipsWhatIsNotPendingOrNotTheShops()
    {
        var f = new Fixture();
        var pending = f.Orders.Seed(Buyer, StoreOrderStatus.Pending).StoreOrders[0].Id;
        var shipped = f.Orders.Seed(Buyer, StoreOrderStatus.Shipped).StoreOrders[0].Id;
        var otherShop = f.Orders.Seed(Buyer, StoreOrderStatus.Pending, vendorId: 20).StoreOrders[0].Id;

        var result = (await f.Service().ConfirmManyAsync(10, [pending, shipped, otherShop], 50, CancellationToken.None)).Value!;

        Assert.Equal([pending], result.Confirmed);
        Assert.Equal([shipped, otherShop], result.Skipped);
        Assert.Contains("storeOrderIds", (await f.Service().ConfirmManyAsync(10, [], 50, CancellationToken.None)).Errors.Keys);
    }

    // ---- Automation ----

    [Fact]
    public async Task AutomationCancelsLateOrdersAndMovesShippedAndDeliveredOnes()
    {
        var f = new Fixture { EmailEnabled = true, Options = new OrderOptions { AutoDeliverAfterDays = 7, AutoCompleteAfterDays = 7 } };
        var late = f.Orders.Seed(Buyer, StoreOrderStatus.Pending, due: Now.AddMinutes(-1)).StoreOrders[0];
        var notYet = f.Orders.Seed(Buyer, StoreOrderStatus.Pending, due: Now.AddHours(1)).StoreOrders[0];
        var shippedLongAgo = f.Orders.Seed(Buyer, StoreOrderStatus.Shipped, due: Now.AddDays(-8)).StoreOrders[0];
        var shippedRecently = f.Orders.Seed(Buyer, StoreOrderStatus.Shipped, due: Now.AddDays(-2)).StoreOrders[0];
        var deliveredLongAgo = f.Orders.Seed(Buyer, StoreOrderStatus.Delivered, due: Now.AddDays(-8)).StoreOrders[0];

        var result = await f.Automation().RunAsync(CancellationToken.None);

        Assert.Equal(new AutomationResult(1, 1, 1), result);
        Assert.Equal((StoreOrderStatus.Cancelled, StoreOrderStatus.Pending), (late.Status, notYet.Status));
        Assert.Equal((StoreOrderStatus.Delivered, StoreOrderStatus.Shipped), (shippedLongAgo.Status, shippedRecently.Status));
        Assert.Equal(StoreOrderStatus.Completed, deliveredLongAgo.Status);

        var cancel = f.Orders.Transitions.Single(t => t.To == StoreOrderStatus.Cancelled);
        Assert.Equal((OrderActorType.System, "not_confirmed_in_time", true), (cancel.ActorType, cancel.Reason, cancel.Restock));
        Assert.Equal((PaymentStatus?)PaymentStatus.Paid, f.Orders.Transitions.Single(t => t.To == StoreOrderStatus.Delivered).NewPaymentStatus);
        Assert.Contains(f.Email.Sent, m => m.ToAddress == "buyer@test" && m.Subject.Contains("cancelled", StringComparison.Ordinal));

        // Running again finds nothing left to do.
        Assert.Equal(new AutomationResult(0, 0, 0), await f.Automation().RunAsync(CancellationToken.None));
    }

    // ---- Helpers ----

    private static CartLineView Line(
        int id, int vendorId, decimal price = 10m, int quantity = 1, bool tracked = false, int? combinationId = null,
        string valueKey = "", string? variant = null, IReadOnlyList<string>? issues = null) =>
        new(id, ProductId: id * 100, $"Product {id}", vendorId, $"Shop {vendorId}", MainPictureId: 0, variant, Sku: null, quantity,
            price, ComparePrice: null, LineTotal: price * quantity, AppliedRule: null,
            AvailableQuantity: tracked ? 50 : null, PreviousUnitPrice: null, issues ?? [], combinationId, valueKey);

    private static PlaceOrderCommand Command(int[] ids, decimal expectedTotal, Dictionary<int, string?>? notes = null) =>
        new(ids, AddressId: 1, PaymentMethod.CashOnDelivery, notes, expectedTotal, "key-1");

    private static OrderCaller AsCustomer(int customerId) => new(customerId, IsAdmin: false, MemberVendorId: null);

    private static readonly OrderCaller ShopMember = new(50, IsAdmin: false, MemberVendorId: 10);
    private static readonly OrderCaller OtherShop = new(51, IsAdmin: false, MemberVendorId: 20);
    private static readonly OrderCaller Admin = new(1, IsAdmin: true, MemberVendorId: null);

    private static ChangeStoreOrderStatusCommand To(StoreOrderStatus status, string? reason = null, string? note = null, string? carrier = null, string? tracking = null) =>
        new(status, reason, note, carrier, tracking);

    private static ChangeStoreOrderStatusCommand Cancel(string? reason = CustomerCancelReasons.ChangedMind, string? note = null) =>
        new(StoreOrderStatus.Cancelled, reason, note);

    private sealed class Fixture
    {
        public FakeCart Cart { get; } = new();
        public InMemoryOrderStore Orders { get; } = new();
        public ProductOwnershipTests.NoMembers Members { get; } = new();
        public RecordingAuditLog Audit { get; } = new();
        public RecordingEmailSender Email { get; } = new();
        public OrderOptions Options { get; set; } = new();
        public bool EmailEnabled { get; set; }

        private OrderNotifier Notifier() => new(Email, Members, TestOptions.Email(EmailEnabled), NullLogger<OrderNotifier>.Instance);

        public CheckoutService Checkout() => new(
            Cart, Orders, new Addresses(), new FakeCustomerIdentityStore(new Customer { Id = Buyer, Email = "buyer@test" }), Members, Audit,
            Notifier(), new FixedClock(), Microsoft.Extensions.Options.Options.Create(Options));

        public OrderService Service() => new(Orders, Cart, new FakeCustomerIdentityStore(new Customer { Id = Buyer, Email = "buyer@test" }), Audit, Notifier(), new FixedClock());

        public OrderAutomation Automation() => new(Orders, new FakeCustomerIdentityStore(new Customer { Id = Buyer, Email = "buyer@test" }), Audit, Notifier(),
            new FixedClock(), Microsoft.Extensions.Options.Options.Create(Options));
    }

    private sealed class FixedClock : Core.Time.IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FakeCart : ICartService
    {
        public List<CartLineView> Lines { get; } = [];
        public Dictionary<int, string> Refuse { get; } = [];
        public List<AddToCartCommand> Added { get; } = [];

        public void Add(CartLineView line) => Lines.Add(line);

        public Task<CartView> GetAsync(int customerId, CancellationToken cancellationToken)
        {
            var groups = Lines.GroupBy(l => l.VendorId).Select(g => new CartShopGroup(g.Key, g.First().VendorName, g.ToList(), g.Sum(l => l.LineTotal))).ToList();
            return Task.FromResult(new CartView("USD", groups, groups.Sum(g => g.Subtotal), Lines.Sum(l => l.Quantity), CartRules.CanCheckout(Lines)));
        }

        public Task<CatalogResult<CartView>> AddAsync(int customerId, AddToCartCommand command, CancellationToken cancellationToken)
        {
            if (Refuse.TryGetValue(command.ProductId, out var code)) return Task.FromResult(CatalogResult.Error<CartView>(code));
            Added.Add(command);
            return GetAsync(customerId, cancellationToken).ContinueWith(t => CatalogResult.Success(t.Result), TaskScheduler.Default);
        }

        public Task<int> CountAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(Added.Sum(a => a.Quantity));
        public Task<CatalogResult<CartView>> SetQuantityAsync(int customerId, int lineId, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> RemoveAsync(int customerId, int lineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> ClearAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> AcceptPricesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class InMemoryOrderStore : IOrderStore
    {
        private int nextOrderId = 1;
        private int nextStoreOrderId = 1;

        public List<CustomerOrder> Placed { get; } = [];
        public List<StoreOrderTransition> Transitions { get; } = [];
        public IReadOnlyList<int>? LastCartItemIds { get; private set; }
        public PlaceOrderOutcome NextOutcome { get; set; } = PlaceOrderOutcome.Created;
        public bool RejectNextTransition { get; set; }

        public Task<PlaceOrderStoreResult> PlaceAsync(CustomerOrder order, IReadOnlyList<int> cartItemIds, CancellationToken cancellationToken)
        {
            if (NextOutcome != PlaceOrderOutcome.Created) return Task.FromResult(new PlaceOrderStoreResult(NextOutcome));
            if (Placed.FirstOrDefault(o => o.CustomerId == order.CustomerId && o.IdempotencyKey == order.IdempotencyKey) is { } existing)
                return Task.FromResult(new PlaceOrderStoreResult(PlaceOrderOutcome.Duplicate, existing.Id));

            LastCartItemIds = cartItemIds;
            order.Id = nextOrderId++;
            order.OrderNumber = OrderRules.OrderNumber(order.CreatedOnUtc, order.Id);
            for (var i = 0; i < order.StoreOrders.Count; i++)
            {
                order.StoreOrders[i].Id = nextStoreOrderId++;
                order.StoreOrders[i].OrderId = order.Id;
                order.StoreOrders[i].SubOrderNumber = OrderRules.SubOrderNumber(order.OrderNumber, i + 1);
            }
            Placed.Add(order);
            return Task.FromResult(new PlaceOrderStoreResult(PlaceOrderOutcome.Created, order.Id));
        }

        public Task<int?> FindIdByIdempotencyKeyAsync(int customerId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(Placed.FirstOrDefault(o => o.CustomerId == customerId && o.IdempotencyKey == idempotencyKey)?.Id);

        public Task<CustomerOrder?> GetAsync(int orderId, CancellationToken cancellationToken) =>
            Task.FromResult(Placed.FirstOrDefault(o => o.Id == orderId));

        public Task<(StoreOrder StoreOrder, int CustomerId)?> GetStoreOrderAsync(int storeOrderId, CancellationToken cancellationToken)
        {
            var order = Placed.FirstOrDefault(o => o.StoreOrders.Any(s => s.Id == storeOrderId));
            return Task.FromResult(order is null ? ((StoreOrder, int)?)null : (order.StoreOrders.First(s => s.Id == storeOrderId), order.CustomerId));
        }

        public Task<StoreOrderPage> ListForCustomerAsync(StoreOrderQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoreOrderPage> ListForVendorAsync(VendorOrderQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();
        public List<(int Id, string Carrier, string Tracking)> ShipmentUpdates { get; } = [];

        public Task<bool> UpdateShipmentAsync(int storeOrderId, string carrier, string trackingNumber, int actorCustomerId, DateTime nowUtc, CancellationToken cancellationToken)
        {
            var storeOrder = Placed.SelectMany(o => o.StoreOrders).First(s => s.Id == storeOrderId);
            if (storeOrder.Status != StoreOrderStatus.Shipped) return Task.FromResult(false);
            ShipmentUpdates.Add((storeOrderId, carrier, trackingNumber));
            (storeOrder.Carrier, storeOrder.TrackingNumber) = (carrier, trackingNumber);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<int>> FindDueAsync(StoreOrderStatus status, DateTime dueBeforeUtc, int take, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<int>>(Placed.SelectMany(o => o.StoreOrders)
                .Where(s => s.Status == status && (status switch
                {
                    StoreOrderStatus.Pending => s.ConfirmByUtc,
                    StoreOrderStatus.Shipped => s.ShippedOnUtc,
                    _ => s.DeliveredOnUtc
                }) < dueBeforeUtc)
                .Select(s => s.Id).Take(take).ToList());

        public Task<bool> TransitionAsync(StoreOrderTransition transition, CancellationToken cancellationToken)
        {
            if (RejectNextTransition)
            {
                RejectNextTransition = false;
                return Task.FromResult(false);
            }
            Transitions.Add(transition);
            var storeOrder = Placed.SelectMany(o => o.StoreOrders).First(s => s.Id == transition.StoreOrderId);
            storeOrder.Status = transition.To;
            if (transition.NewPaymentStatus is { } payment) storeOrder.PaymentStatus = payment;
            if (transition.To == StoreOrderStatus.Shipped) (storeOrder.Carrier, storeOrder.TrackingNumber) = (transition.Carrier, transition.TrackingNumber);
            // Like the SQL store: the moment of the new status is stamped, so the next automatic step starts counting from it.
            if (transition.To == StoreOrderStatus.Shipped) storeOrder.ShippedOnUtc = transition.NowUtc;
            if (transition.To == StoreOrderStatus.Delivered) storeOrder.DeliveredOnUtc = transition.NowUtc;
            return Task.FromResult(true);
        }

        public CustomerOrder Seed(int customerId, StoreOrderStatus status, IReadOnlyList<(int ProductId, string Name, string ValueIds, int Quantity)>? items = null,
            PaymentStatus payment = PaymentStatus.Pending, DateTime? due = null, int vendorId = 10)
        {
            var order = new CustomerOrder
            {
                Id = nextOrderId++, CustomerId = customerId, CurrencyCode = "USD", IdempotencyKey = Guid.NewGuid().ToString(), CreatedOnUtc = Now,
                StoreOrders =
                [
                    new StoreOrder
                    {
                        Id = nextStoreOrderId++, VendorId = vendorId, Status = status, PaymentStatus = payment, SubOrderNumber = "NM260101-0001-1",
                        ConfirmByUtc = due ?? Now.AddDays(2), ShippedOnUtc = due, DeliveredOnUtc = due,
                        Items = (items ?? [(1, "Mug", "", 1)]).Select(i => new OrderItem { ProductId = i.ProductId, ProductName = i.Name, ValueIds = i.ValueIds, Quantity = i.Quantity }).ToList()
                    }
                ]
            };
            order.StoreOrders[0].OrderId = order.Id;
            Placed.Add(order);
            return order;
        }
    }

    private sealed class Addresses : ICustomerAccountDataStore
    {
        public Task<CustomerAddress?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) =>
            Task.FromResult(customerId == Buyer && addressId == 1
                ? new CustomerAddress { Id = 1, CustomerId = Buyer, FirstName = "An", LastName = "Nguyen", Address1 = "1 Pho Hue", City = "Hanoi", CountryCode = "VN", PhoneNumber = "+84 900" }
                : null);

        public Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CreateEmailChangeAsync(int customerId, string newEmail, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(int CustomerId, string NewEmail)?> ConsumeEmailChangeAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ApplyEmailChangeAsync(int customerId, string newEmail, DateTime nowUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
