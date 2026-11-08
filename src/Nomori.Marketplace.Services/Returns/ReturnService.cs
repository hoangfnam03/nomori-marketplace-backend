using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Returns;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Returns;

/// <summary>
/// Return requests on a delivered shop order: the customer asks, the shop (or an administrator) approves or rejects and confirms the goods came
/// back, an administrator sends the money back. Each step is its own compare-and-set, so a double click or two people never do a step twice.
/// </summary>
public sealed partial class ReturnService(
    IReturnStore store,
    IOrderStore orders,
    IPrimaryCurrencyProvider primaryCurrency,
    IInventoryService inventory,
    IPaymentService payments,
    IReturnShipper shipper,
    ICustomerIdentityStore customers,
    IEmailQueueService emailQueue,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<ReturnOptions> options,
    ILogger<ReturnService> logger) : IReturnService
{
    // ---- Customers ----

    public async Task<CatalogResult<ReturnRequest>> RequestAsync(int customerId, RequestReturnCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var reason = command.Reason?.Trim().ToLowerInvariant() ?? string.Empty;
        var note = command.Note?.Trim();
        var wanted = command.Lines ?? [];

        if (!ReturnReasons.All.Contains(reason)) errors["reason"] = ["Choose a reason."];
        if (note?.Length > ReturnLimits.MaxNoteLength) errors["note"] = [$"The note can have at most {ReturnLimits.MaxNoteLength} characters."];
        if (wanted.Count is 0 or > ReturnLimits.MaxLines) errors["lines"] = [$"Choose 1 to {ReturnLimits.MaxLines} items to return."];
        else if (wanted.Any(l => l.Quantity < 1) || wanted.Select(l => l.OrderLineId).Distinct().Count() != wanted.Count)
            errors["lines"] = ["Every item appears once, with a quantity of at least 1."];
        if (errors.Count > 0) return CatalogResult.Failure<ReturnRequest>(errors);

        var shopOrder = await orders.GetShopOrderAsync(command.ShopOrderId, cancellationToken);
        // Someone else's order is not found, so ids reveal nothing.
        if (shopOrder?.Order is null || shopOrder.Order.CustomerId != customerId) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);

        var history = await orders.GetHistoryAsync([shopOrder.Id], cancellationToken);
        var deliveredOn = history.Where(h => h.ToStatus == ShopOrderStatus.Delivered).Select(h => (DateTime?)h.CreatedOnUtc).Max();
        var now = clock.UtcNow;
        if (!ReturnRules.IsEligible(shopOrder.Status, deliveredOn, now, options.Value.WindowDays))
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.NotEligible);

        var lineById = shopOrder.Lines.ToDictionary(l => l.Id);
        if (wanted.Any(l => !lineById.ContainsKey(l.OrderLineId))) return CatalogResult.Failure<ReturnRequest>("lines", "An item is not part of this order.");
        // An item goes back whole: every unit that was bought, never part of them.
        if (wanted.Any(l => l.Quantity != lineById[l.OrderLineId].Quantity))
            return CatalogResult.Failure<ReturnRequest>("lines", "An item is returned whole, with every unit that was bought.");

        var held = await store.HeldQuantitiesAsync(shopOrder.Id, cancellationToken);
        if (wanted.Any(l => l.Quantity + held.GetValueOrDefault(l.OrderLineId) > lineById[l.OrderLineId].Quantity))
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.QuantityExceeded);

        var places = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).DecimalPlaces;
        var lines = wanted.Select(w =>
        {
            var line = lineById[w.OrderLineId];
            return new ReturnLine
            {
                OrderLineId = line.Id, Name = line.Name, VariantLabel = line.VariantLabel, ProductId = line.ProductId,
                CombinationId = line.CombinationId, Quantity = w.Quantity, Amount = ReturnRules.RefundFor(line, w.Quantity, shopOrder, places)
            };
        }).ToList();

        var (saved, exceeded) = await store.InsertAsync(new ReturnRequest
        {
            ShopOrderId = shopOrder.Id, OrderId = shopOrder.OrderId, ShopOrderNumber = shopOrder.Number, VendorId = shopOrder.VendorId,
            ShopName = shopOrder.ShopName, CustomerId = customerId, Status = ReturnStatus.Requested, Reason = reason,
            CustomerNote = string.IsNullOrEmpty(note) ? null : note, CurrencyCode = shopOrder.Order.CurrencyCode,
            RefundAmount = lines.Sum(l => l.Amount), CreatedOnUtc = now, UpdatedOnUtc = now, Lines = lines
        }, cancellationToken);
        // Another request took the quantity between the check and the write.
        if (exceeded || saved is null) return CatalogResult.Error<ReturnRequest>(ReturnErrors.QuantityExceeded);

        await auditLog.WriteAsync("return.requested", customerId, entityType: "ReturnRequest", entityId: saved.Id,
            details: new { returnId = saved.Id, saved.Number, saved.ShopOrderId, saved.RefundAmount, lines = saved.Lines.Count }, cancellationToken: cancellationToken);
        return CatalogResult.Success(saved);
    }

    public Task<PagedResult<ReturnRequest>> GetMineAsync(int customerId, int page, int pageSize, CancellationToken cancellationToken) =>
        store.GetPagedAsync(Clamp(new ReturnQuery(customerId, null, null, page, pageSize)), cancellationToken);

    public async Task<CatalogResult<ReturnRequest>> GetMineAsync(int customerId, int id, CancellationToken cancellationToken)
    {
        var request = await store.GetAsync(id, cancellationToken);
        return request is null || request.CustomerId != customerId
            ? CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound)
            : CatalogResult.Success(request);
    }

    public async Task<CatalogResult<ReturnRequest>> WithdrawAsync(int customerId, int id, CancellationToken cancellationToken)
    {
        var request = await store.GetAsync(id, cancellationToken);
        if (request is null || request.CustomerId != customerId) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);
        return await MoveAsync(request, ReturnAction.Withdraw, new ReturnCaller(OrderActor.Customer, customerId, null), null, null, null, cancellationToken);
    }

    // ---- Shops and administrators ----

    public Task<PagedResult<ReturnRequest>> GetAsync(ReturnCaller caller, ReturnStatus? status, int page, int pageSize, CancellationToken cancellationToken) =>
        // The shop comes from the caller and replaces whatever else was asked.
        store.GetPagedAsync(Clamp(new ReturnQuery(null, caller.Actor == OrderActor.Shop ? caller.VendorId : null, status, page, pageSize)), cancellationToken);

    public async Task<CatalogResult<ReturnRequest>> GetAsync(ReturnCaller caller, int id, CancellationToken cancellationToken)
    {
        var request = await VisibleAsync(caller, id, cancellationToken);
        return request is null ? CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound) : CatalogResult.Success(request);
    }

    public async Task<CatalogResult<ReturnRequest>> ApproveAsync(ReturnCaller caller, int id, string? note, CancellationToken cancellationToken)
    {
        var request = await VisibleAsync(caller, id, cancellationToken);
        if (request is null) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);
        var text = note?.Trim();
        if (text?.Length > ReturnLimits.MaxNoteLength) return CatalogResult.Failure<ReturnRequest>("note", $"The note can have at most {ReturnLimits.MaxNoteLength} characters.");
        var approved = await MoveAsync(request, ReturnAction.Approve, caller, string.IsNullOrEmpty(text) ? null : text, null, null, cancellationToken, notify: false);
        if (!approved.Succeeded) return approved;

        // The carrier comes for the goods at the customer's address. A parcel that cannot be made leaves the return approved: the shop tries
        // again, and the customer is told either way.
        await MakeShipmentAsync(approved.Value!, caller.ActorCustomerId, cancellationToken);
        var updated = (await store.GetAsync(id, cancellationToken))!;
        await NotifyAsync(updated, cancellationToken);
        return CatalogResult.Success(updated);
    }

    public async Task<CatalogResult<ReturnRequest>> ShipBackAsync(ReturnCaller caller, int id, CancellationToken cancellationToken)
    {
        var request = await VisibleAsync(caller, id, cancellationToken);
        if (request is null) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);
        if (request.Status != ReturnStatus.Approved || request.ReturnTrackingNumber is not null)
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.ShipmentNotNeeded);

        var shipment = await MakeShipmentAsync(request, caller.ActorCustomerId, cancellationToken);
        if (shipment is null) return CatalogResult.Error<ReturnRequest>(ReturnErrors.NoCarrier);
        if (!shipment.Succeeded) return CatalogResult.Failure<ReturnRequest>("shipment", shipment.Error ?? "The carrier refused the parcel.");

        var updated = (await store.GetAsync(id, cancellationToken))!;
        await NotifyAsync(updated, cancellationToken);
        return CatalogResult.Success(updated);
    }

    public async Task<CatalogResult<ReturnRequest>> RejectAsync(ReturnCaller caller, int id, string? note, CancellationToken cancellationToken)
    {
        var request = await VisibleAsync(caller, id, cancellationToken);
        if (request is null) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);
        var text = note?.Trim() ?? string.Empty;
        if (text.Length is 0 or > ReturnLimits.MaxNoteLength)
            return CatalogResult.Failure<ReturnRequest>("note", $"Give a reason of 1 to {ReturnLimits.MaxNoteLength} characters.");
        return await MoveAsync(request, ReturnAction.Reject, caller, text, null, null, cancellationToken);
    }

    public async Task<CatalogResult<ReturnRequest>> ReceiveAsync(ReturnCaller caller, int id, bool restock, CancellationToken cancellationToken)
    {
        var request = await VisibleAsync(caller, id, cancellationToken);
        if (request is null) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);

        var moved = await MoveAsync(request, ReturnAction.Receive, caller, null, restock, null, cancellationToken);
        if (!moved.Succeeded) return moved;
        // The step is done once (compare-and-set), so the stock goes back once too.
        if (restock) await RestockAsync(request, caller, cancellationToken);

        // Paid online: the money goes back at once, through the same gateway. Cash on delivery or a transfer needs a person to send it, so an
        // administrator refunds it by hand; so does a refund the gateway refuses.
        var payment = await FindRefundablePaymentAsync(request, cancellationToken);
        if (payment is null || !await IsOnlineAsync(payment, cancellationToken)) return moved;
        // Nothing to send back (a fully discounted item, or the return shipping took it all): just close it.
        if (ReturnRules.PayOut(request) <= 0) payment = null;
        var refunded = await RefundThroughAsync(request, payment, manual: false, caller.ActorCustomerId, automatic: true, cancellationToken);
        return refunded.Succeeded ? refunded : CatalogResult.Success((await store.GetAsync(id, cancellationToken))!);
    }

    // ---- Administrators ----

    public async Task<CatalogResult<ReturnRequest>> RefundAsync(ReturnCaller caller, int id, bool manual, CancellationToken cancellationToken)
    {
        var request = await VisibleAsync(caller, id, cancellationToken);
        if (request is null) return CatalogResult.Error<ReturnRequest>(CatalogErrors.NotFound);
        if (ReturnRules.Transition(request.Status, ReturnAction.Refund, caller.Actor) is null)
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.InvalidTransition);

        // Find the payment first: a refund that cannot go anywhere must not use up the step.
        PaymentTransaction? payment = null;
        if (!manual && ReturnRules.PayOut(request) > 0)
        {
            payment = await FindRefundablePaymentAsync(request, cancellationToken);
            if (payment is null) return CatalogResult.Error<ReturnRequest>(ReturnErrors.NoRefundablePayment);
        }

        return await RefundThroughAsync(request, payment, manual, caller.ActorCustomerId, automatic: false, cancellationToken);
    }

    /// <summary>
    /// Sends the refund of a received return through <paramref name="payment"/> (null: nothing to send, the money went back by hand) and marks
    /// it refunded. The step is claimed first, so of two attempts only one moves money.
    /// </summary>
    private async Task<CatalogResult<ReturnRequest>> RefundThroughAsync(
        ReturnRequest request, PaymentTransaction? payment, bool manual, int actorCustomerId, bool automatic, CancellationToken cancellationToken)
    {
        var id = request.Id;
        // Claim the step before moving money: of two clicks only one gets here, so the customer is never paid twice.
        var now = clock.UtcNow;
        if (!await store.TryTransitionAsync(new ReturnTransition(id, ReturnStatus.Received, ReturnStatus.Refunded, null, null, payment?.Id, now), cancellationToken))
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.InvalidTransition);

        if (payment is not null)
        {
            var refund = await payments.RefundAsync(payment.Id, ReturnRules.PayOut(request), actorCustomerId, cancellationToken);
            if (!refund.Succeeded)
            {
                // Nothing was sent: give the step back so it can be tried again.
                await store.TryTransitionAsync(new ReturnTransition(id, ReturnStatus.Refunded, ReturnStatus.Received, null, null, null, clock.UtcNow), CancellationToken.None);
                return refund.Errors.Count > 0
                    ? CatalogResult.Failure<ReturnRequest>(refund.Errors)
                    : CatalogResult.Error<ReturnRequest>(refund.ErrorCode ?? ReturnErrors.NoRefundablePayment);
            }
        }

        await auditLog.WriteAsync("return.refunded", actorCustomerId, entityType: "ReturnRequest", entityId: id,
            details: new { returnId = id, request.Number, request.RefundAmount, payOut = ReturnRules.PayOut(request), manual, automatic, paymentId = payment?.Id },
            cancellationToken: cancellationToken);
        await MarkReturnedWhenWholeAsync(request.ShopOrderId, cancellationToken);
        var updated = (await store.GetAsync(id, cancellationToken))!;
        await NotifyAsync(updated, cancellationToken);
        return CatalogResult.Success(updated);
    }

    // ---- Helpers ----

    /// <summary>When the refund just made was the last item of the shop order, the shop order becomes returned (the system does it).</summary>
    private async Task MarkReturnedWhenWholeAsync(int shopOrderId, CancellationToken cancellationToken)
    {
        var shopOrder = await orders.GetShopOrderAsync(shopOrderId, cancellationToken);
        if (shopOrder is null || OrderRules.Transition(shopOrder.Status, OrderAction.Return, OrderActor.System) is not { } target) return;
        if (!ReturnRules.IsReturnedWhole(shopOrder.Lines, await store.RefundedQuantitiesAsync(shopOrderId, cancellationToken))) return;

        // Compare-and-set: a second refund at the same moment does not move it twice.
        if (await orders.TryTransitionAsync(new ShopOrderTransition(
                shopOrderId, shopOrder.Status, target, OrderActor.System, null, "Every item was returned and refunded", null, null, null, clock.UtcNow), cancellationToken))
            await auditLog.WriteAsync("order.shop_order_changed", entityType: "ShopOrder", entityId: shopOrderId,
                details: new { shopOrderId, shopOrder.OrderId, from = OrderRules.ToWire(shopOrder.Status), to = OrderRules.ToWire(target), actor = "system" },
                cancellationToken: cancellationToken);
    }

    /// <summary>A shop sees only its own returns; an administrator all of them. Anything else does not exist for the caller.</summary>
    private async Task<ReturnRequest?> VisibleAsync(ReturnCaller caller, int id, CancellationToken cancellationToken)
    {
        var request = await store.GetAsync(id, cancellationToken);
        if (request is null) return null;
        return caller.Actor switch
        {
            OrderActor.Admin => request,
            OrderActor.Shop when caller.VendorId == request.VendorId => request,
            _ => null
        };
    }

    private async Task<CatalogResult<ReturnRequest>> MoveAsync(
        ReturnRequest request, ReturnAction action, ReturnCaller caller, string? note, bool? restock, int? paymentId, CancellationToken cancellationToken,
        bool notify = true)
    {
        if (ReturnRules.Transition(request.Status, action, caller.Actor) is not { } target)
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.InvalidTransition);

        if (!await store.TryTransitionAsync(new ReturnTransition(request.Id, request.Status, target, note, restock, paymentId, clock.UtcNow), cancellationToken))
            return CatalogResult.Error<ReturnRequest>(ReturnErrors.InvalidTransition);

        await auditLog.WriteAsync("return.status_changed", caller.ActorCustomerId, entityType: "ReturnRequest", entityId: request.Id,
            details: new { returnId = request.Id, request.Number, from = ReturnRules.ToWire(request.Status), to = ReturnRules.ToWire(target), actor = OrderRules.ToWire(caller.Actor), restock },
            cancellationToken: cancellationToken);

        var updated = (await store.GetAsync(request.Id, cancellationToken))!;
        if (notify && target is ReturnStatus.Approved or ReturnStatus.Rejected) await NotifyAsync(updated, cancellationToken);
        return CatalogResult.Success(updated);
    }

    /// <summary>
    /// Asks the carrier for the parcel back and writes what came of it, with who pays for it. Null when no carrier is set up. A carrier that
    /// throws is an error like any other: the approval stands.
    /// </summary>
    private async Task<ReturnShipment?> MakeShipmentAsync(ReturnRequest request, int actorCustomerId, CancellationToken cancellationToken)
    {
        ReturnShipment? shipment;
        try
        {
            shipment = await shipper.ShipBackAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogShipmentFailed(exception, request.Number);
            shipment = new ReturnShipment(null, 0m, "The carrier could not be reached.");
        }
        if (shipment is null) return null;

        var payer = ReturnRules.ShippingPayer(request.Reason);
        await store.SetShipmentAsync(request.Id, shipment, payer, clock.UtcNow, cancellationToken);
        await auditLog.WriteAsync(shipment.Succeeded ? "return.shipment_created" : "return.shipment_failed", actorCustomerId,
            entityType: "ReturnRequest", entityId: request.Id,
            details: new { returnId = request.Id, request.Number, shipment.TrackingNumber, shipment.Fee, payer, shipment.Error }, cancellationToken: cancellationToken);
        return shipment;
    }

    /// <summary>Whether the payment went through a gateway, which can send money back by itself; cash on delivery and transfers cannot.</summary>
    private async Task<bool> IsOnlineAsync(PaymentTransaction payment, CancellationToken cancellationToken) =>
        (await payments.GetMethodsAsync(cancellationToken)).Any(m =>
            string.Equals(m.SystemName, payment.Method, StringComparison.OrdinalIgnoreCase) && m.Kind != PaymentProviderKind.Offline);

    private async Task<PaymentTransaction?> FindRefundablePaymentAsync(ReturnRequest request, CancellationToken cancellationToken)
    {
        foreach (var paymentId in await store.PaymentIdsForOrderAsync(request.OrderId, cancellationToken))
        {
            var payment = await payments.GetPaymentAsync(paymentId, cancellationToken);
            if (payment is not null && PaymentRules.CanRefund(payment.Status) && PaymentRules.Refundable(payment) >= ReturnRules.PayOut(request)) return payment;
        }
        return null;
    }

    private async Task RestockAsync(ReturnRequest request, ReturnCaller caller, CancellationToken cancellationToken)
    {
        var lines = request.Lines.Select(l => new StockReturnLine(l.ProductId, l.CombinationId, l.Quantity)).ToList();
        var back = await inventory.ReturnToStockAsync(request.Number, lines, cancellationToken);

        // The receipt stays done; stock that did not go back is for people to look at.
        if (!back.Succeeded)
            await auditLog.WriteAsync("return.restock_failed", caller.ActorCustomerId, entityType: "ReturnRequest", entityId: request.Id,
                details: new { returnId = request.Id, request.Number }, cancellationToken: cancellationToken);
    }

    /// <summary>How the goods go back: a carrier comes for them, or the customer sends them.</summary>
    private static string ShipBackText(ReturnRequest request)
    {
        if (request.ReturnTrackingNumber is not { } tracking) return "Send the items back to the shop.";
        var fee = request.ReturnShippingPaidBy == ReturnPayers.Customer && request.ReturnShippingFee > 0
            ? $" The return shipping of {request.ReturnShippingFee.ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture)} {WebUtility.HtmlEncode(request.CurrencyCode)} is taken off your refund."
            : string.Empty;
        return $"A GHN driver will collect the items at your address (parcel {WebUtility.HtmlEncode(tracking)}): pack them and keep them ready.{fee}";
    }

    /// <summary>Best effort: a failure to write the email never undoes the decision.</summary>
    private async Task NotifyAsync(ReturnRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var customer = await customers.FindByIdAsync(request.CustomerId, cancellationToken);
            if (customer is null || !customer.Active) return;

            var number = WebUtility.HtmlEncode(request.Number);
            var greeting = string.IsNullOrWhiteSpace(customer.FirstName) ? "Hello," : $"Hello {WebUtility.HtmlEncode(customer.FirstName)},";
            var note = string.IsNullOrWhiteSpace(request.ResolutionNote) ? string.Empty : $"<p>Note from the shop: {WebUtility.HtmlEncode(request.ResolutionNote)}</p>";
            var (kind, subject, text) = request.Status switch
            {
                ReturnStatus.Approved => (EmailKinds.ReturnApproved, $"Your return {request.Number} was approved",
                    $"<p>Your return <strong>{number}</strong> was approved. {ShipBackText(request)} You get your money back once the shop receives them.</p>{note}"),
                ReturnStatus.Rejected => (EmailKinds.ReturnRejected, $"Your return {request.Number} was not approved",
                    $"<p>Your return <strong>{number}</strong> was not approved.</p>{note}"),
                ReturnStatus.Refunded => (EmailKinds.ReturnRefunded, $"Your refund for return {request.Number}",
                    $"<p>Your return <strong>{number}</strong> is complete. We refunded {ReturnRules.PayOut(request).ToString("#,0.##", System.Globalization.CultureInfo.InvariantCulture)} {WebUtility.HtmlEncode(request.CurrencyCode)}.</p>"),
                _ => (string.Empty, string.Empty, string.Empty)
            };
            if (kind.Length == 0) return;
            await emailQueue.EnqueueAsync(kind, new EmailMessage(customer.Email, subject, $"<p>{greeting}</p>{text}"), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotifyFailed(exception, request.Number);
        }
    }

    private static ReturnQuery Clamp(ReturnQuery query) =>
        query with { Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, ReturnLimits.MaxPageSize) };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not queue the email of return {Number}.")]
    private partial void LogNotifyFailed(Exception exception, string number);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not make the parcel back of return {Number}.")]
    private partial void LogShipmentFailed(Exception exception, string number);
}
