using System.Globalization;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Shipping;

public sealed class GhnShippingService(
    IGhnClient client,
    IGhnStore store,
    IOrderService orderService,
    ITrackingService trackingService,
    IPrimaryCurrencyProvider primaryCurrency,
    ICurrencyService currencyService,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<GhnOptions> options) : IGhnShippingService
{
    private readonly GhnOptions settings = options.Value;

    public bool IsConfigured => settings.IsConfigured;

    // ---- Address picker (two-level units) ----

    public Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken) =>
        IsConfigured ? client.GetProvincesAsync(cancellationToken) : Task.FromResult(GhnResult.Fail<IReadOnlyList<GhnProvince>>("GHN is not configured."));

    public Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int provinceId, CancellationToken cancellationToken) =>
        IsConfigured ? client.GetWardsAsync(provinceId, cancellationToken) : Task.FromResult(GhnResult.Fail<IReadOnlyList<GhnWard>>("GHN is not configured."));

    // ---- Checkout ----

    public async Task<ShippingOption?> QuoteAsync(int vendorId, GhnPlace? destination, IReadOnlyList<GhnLineRef> lines, decimal subtotal, CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return null;
        // Without GHN (a machine with no token) every shop ships at the fallback fee, so checkout keeps working.
        if (!IsConfigured) return GhnRules.Option(await ToPrimaryAsync(settings.FallbackFeeVnd, cancellationToken), true, subtotal, null);

        // A shop without a pickup address, or an address without GHN's place, does not ship.
        if (await store.GetPickupAsync(vendorId, cancellationToken) is not { } pickup || destination is null) return null;

        var parcel = await ParcelOfAsync(lines.Select(l => (l.ProductId, l.Quantity)), cancellationToken);
        var request = new GhnFeeRequest(pickup.Place, destination, parcel, GhnRules.InsuranceOf(await ToVndAsync(subtotal, cancellationToken), settings), settings.ServiceTypeId);
        var feeCall = client.GetFeeAsync(request, cancellationToken);
        var leadtimeCall = client.GetLeadtimeAsync(request, cancellationToken);
        await Task.WhenAll(feeCall, leadtimeCall);
        var (fee, leadtime) = (await feeCall, await leadtimeCall);

        // GHN could not price it: the fallback fee, so the customer can still order.
        var amount = await ToPrimaryAsync(fee.Succeeded ? fee.Value : settings.FallbackFeeVnd, cancellationToken);
        return GhnRules.Option(amount, !fee.Succeeded, subtotal, pickup.FreeShippingOverSubtotal, leadtime.Succeeded ? leadtime.Value : null);
    }

    public async Task<bool> CanShipAsync(int vendorId, CancellationToken cancellationToken) =>
        !IsConfigured || await store.GetPickupAsync(vendorId, cancellationToken) is not null;

    // ---- The shop's pickup address ----

    public Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken) => store.GetPickupAsync(vendorId, cancellationToken);

    public async Task<CatalogResult<GhnPickup>> SavePickupAsync(int vendorId, SaveGhnPickupCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        static string Clean(string? value) => value?.Trim() ?? string.Empty;
        var name = Clean(command.ContactName);
        var phone = new string(Clean(command.Phone).Where(c => !char.IsWhiteSpace(c) && c is not '.' and not '-').ToArray());
        var address = Clean(command.Address);
        var (province, ward) = (Clean(command.ProvinceName), Clean(command.WardName));

        if (name.Length is 0 or > 100) errors["contactName"] = ["Enter the sender's name (at most 100 characters)."];
        if (!GhnRules.IsVietnamesePhone(phone)) errors["phone"] = ["Enter a Vietnamese phone number, for example 0901234567."];
        if (address.Length is 0 or > 200) errors["address"] = ["Enter the house number and street (at most 200 characters)."];
        if (command.FreeShippingOverSubtotal is { } freeOver)
        {
            var places = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).DecimalPlaces;
            if (freeOver <= 0m || freeOver > 1_000_000_000m || !CurrencyRules.HasValidScale(freeOver, places))
                errors["freeShippingOverSubtotal"] = [$"The amount must be above 0, with at most {places} decimal places."];
        }
        if (command.ProvinceId <= 0 || command.WardId <= 0 || province.Length is 0 or > 100 || ward.Length is 0 or > 100)
            errors["wardId"] = ["Choose the province and the ward."];
        if (errors.Count > 0) return CatalogResult.Failure<GhnPickup>(errors);

        var pickup = new GhnPickup
        {
            VendorId = vendorId, ContactName = name, Phone = phone, Address = address, ProvinceId = command.ProvinceId, ProvinceName = province,
            WardId = command.WardId, WardName = ward, FreeShippingOverSubtotal = command.FreeShippingOverSubtotal, UpdatedOnUtc = clock.UtcNow
        };
        await store.SavePickupAsync(pickup, cancellationToken);
        await auditLog.WriteAsync("shipping.ghn_pickup_saved", actorCustomerId, entityType: "Vendor", entityId: vendorId,
            details: new { vendorId, pickup.ProvinceId, pickup.WardId }, cancellationToken: cancellationToken);
        return CatalogResult.Success(pickup);
    }

    public async Task DeletePickupAsync(int vendorId, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (await store.DeletePickupAsync(vendorId, cancellationToken))
            await auditLog.WriteAsync("shipping.ghn_pickup_deleted", actorCustomerId, entityType: "Vendor", entityId: vendorId,
                details: new { vendorId }, cancellationToken: cancellationToken);
    }

    // ---- Shop ----

    public async Task<CatalogResult<ShopOrderDetail>> CreateShipmentAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NotConfigured);

        var detail = await orderService.GetShopOrderAsync(vendorId, shopOrderId, cancellationToken);
        if (!detail.Succeeded) return detail;
        var shopOrder = detail.Value!.ShopOrder;
        if (shopOrder.Status != ShopOrderStatus.Confirmed) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NotConfirmed);

        var destination = await store.GetDestinationAsync(shopOrder.OrderId, cancellationToken);
        if (destination?.Place is not { } to) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NoArea);
        if (await store.GetPickupAsync(vendorId, cancellationToken) is not { } pickup) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NoPickup);

        // Cash on delivery: the GHN driver collects what the customer owes for this shop's parcel.
        var isCod = string.Equals(destination.PaymentMethod, "cod", StringComparison.OrdinalIgnoreCase);
        var cod = isCod ? await ToVndAsync(shopOrder.Total, cancellationToken) : 0L;
        var sizes = await store.GetProductSizesAsync(shopOrder.Lines.Select(l => l.ProductId).ToList(), cancellationToken);
        var items = new List<GhnItem>();
        foreach (var line in shopOrder.Lines)
        {
            var name = line.VariantLabel is null ? line.Name : $"{line.Name} ({line.VariantLabel})";
            var grams = sizes.GetValueOrDefault(line.ProductId)?.WeightGrams ?? settings.ItemWeightGrams;
            items.Add(new GhnItem(name, line.Quantity, grams, await ToVndAsync(line.UnitPrice, cancellationToken)));
        }
        var parcel = GhnRules.Parcel(shopOrder.Lines.Select(l => (l.Quantity, sizes.GetValueOrDefault(l.ProductId))), settings);
        var insurance = GhnRules.InsuranceOf(await ToVndAsync(shopOrder.Subtotal - shopOrder.DiscountAmount, cancellationToken), settings);

        var address = string.Join(", ", new[] { destination.Address1, destination.Address2 }.Where(p => !string.IsNullOrWhiteSpace(p)));
        var content = string.Join(", ", items.Select(i => $"{i.Quantity} x {i.Name}"));
        var created = await client.CreateOrderAsync(new GhnCreateOrderRequest(
            destination.RecipientName, destination.RecipientPhone, address, to.ProvinceId, destination.ProvinceName ?? string.Empty, to.WardId,
            destination.WardName ?? string.Empty, cod, insurance, null, content.Length > 0 ? content[..Math.Min(content.Length, 2000)] : shopOrder.Number,
            parcel, settings.ServiceTypeId, settings.RequiredNote, shopOrder.Number, items, pickup.ToSender()), cancellationToken);
        if (!created.Succeeded) return CatalogResult.Failure<ShopOrderDetail>("ghn", $"GHN: {created.Error}");

        // What GHN really charges, and what it was told, so a later weighing or the books can be checked against it.
        await store.SaveShipmentAsync(new GhnShipment(
            shopOrderId, await ToPrimaryAsync(created.Value!.TotalFeeVnd, cancellationToken), GhnRules.ChargeableWeight(parcel),
            isCod ? shopOrder.Total : 0m, created.Value.ExpectedDeliveryUtc), cancellationToken);
        return await orderService.ShipAsync(vendorId, shopOrderId, new ShipCommand(GhnRules.CarrierName, created.Value.OrderCode), actorCustomerId, cancellationToken);
    }

    public async Task<CatalogResult<ShopOrderDetail>> SyncAsync(int vendorId, int shopOrderId, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NotConfigured);

        var detail = await orderService.GetShopOrderAsync(vendorId, shopOrderId, cancellationToken);
        if (!detail.Succeeded) return detail;
        var shopOrder = detail.Value!.ShopOrder;
        if (!GhnRules.IsGhnOrder(shopOrder)) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NotGhn);

        var ghnOrder = await client.GetOrderAsync(shopOrder.TrackingNumber!, cancellationToken);
        if (!ghnOrder.Succeeded) return CatalogResult.Failure<ShopOrderDetail>("ghn", $"GHN: {ghnOrder.Error}");

        if (shopOrder.Status == ShopOrderStatus.Shipped)
        {
            // Tracking lines can only be added while the parcel is on its way (F20-B D4).
            var tracking = await trackingService.GetTrackingAsync(new TrackingCaller(false, actorCustomerId, vendorId), shopOrderId, cancellationToken);
            var known = tracking.Value?.Events.Select(e => (e.Note, e.OccurredOnUtc)).ToHashSet() ?? [];
            foreach (var log in ghnOrder.Value!.Logs)
            {
                var status = GhnRules.TrackingStatusOf(log.Status) ?? TrackingRules.Info;
                var note = GhnRules.NoteOf(log.Status);
                if (known.Contains((note, log.UpdatedUtc))) continue;
                // A line GHN dated oddly (before the order, or ahead of the clock) is refused by the tracking rules; the others still go in.
                await trackingService.AddEventAsync(vendorId, shopOrderId, new AddTrackingEventCommand(status, null, note, log.UpdatedUtc), actorCustomerId, cancellationToken);
            }

            if (ghnOrder.Value.Status == "delivered") return await orderService.DeliverAsync(vendorId, shopOrderId, actorCustomerId, cancellationToken);
        }
        return await orderService.GetShopOrderAsync(vendorId, shopOrderId, cancellationToken);
    }

    public async Task<CatalogResult<string>> GetLabelUrlAsync(int vendorId, int shopOrderId, CancellationToken cancellationToken)
    {
        if (!IsConfigured) return CatalogResult.Error<string>(GhnErrors.NotConfigured);

        var detail = await orderService.GetShopOrderAsync(vendorId, shopOrderId, cancellationToken);
        if (!detail.Succeeded) return CatalogResult.Error<string>(detail.ErrorCode ?? CatalogErrors.NotFound);
        if (!GhnRules.IsGhnOrder(detail.Value!.ShopOrder)) return CatalogResult.Error<string>(GhnErrors.NotGhn);

        var url = await client.GetLabelUrlAsync(detail.Value.ShopOrder.TrackingNumber!, cancellationToken);
        return url.Succeeded ? CatalogResult.Success(url.Value!) : CatalogResult.Failure<string>("ghn", $"GHN: {url.Error}");
    }

    // ---- Webhook ----

    public bool IsWebhookSecret(string? secret) =>
        !string.IsNullOrEmpty(settings.WebhookSecret) && secret is not null
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(settings.WebhookSecret));

    public async Task<GhnWebhookOutcome> HandleWebhookAsync(GhnWebhook webhook, CancellationToken cancellationToken)
    {
        if (webhook.ShopID != settings.ShopId || string.IsNullOrWhiteSpace(webhook.OrderCode)) return GhnWebhookOutcome.Ignored;
        var target = await store.FindByOrderCodeAsync(webhook.OrderCode.Trim(), cancellationToken);
        if (target is null) return GhnWebhookOutcome.Ignored;

        if (string.Equals(webhook.Type, "switch_status", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(webhook.Status))
            await ApplyStatusAsync(target, webhook, cancellationToken);

        // Every callback carries GHN's weighing: heavier than declared means the shop under-declared and owes the rise in fee.
        await ApplyWeighingAsync(target, webhook, cancellationToken);

        // GHN paid over the cash it collected.
        if (Parse(webhook.CODTransferDate) is { } transferred) await store.MarkCodTransferredAsync(target.ShopOrderId, transferred, cancellationToken);
        return GhnWebhookOutcome.Handled;
    }

    private async Task ApplyStatusAsync(GhnShopOrderRef target, GhnWebhook webhook, CancellationToken cancellationToken)
    {
        var status = webhook.Status!.Trim().ToLowerInvariant();
        var at = Parse(webhook.Time);

        // GHN may send the same event twice: the same line at the same time is not added again.
        var note = GhnRules.NoteOf(status);
        var tracking = await trackingService.GetTrackingAsync(new TrackingCaller(true, 0, null), target.ShopOrderId, cancellationToken);
        var repeat = tracking.Value?.Events.Any(e => e.Note == note && (at is null || e.OccurredOnUtc == at)) == true;
        if (!repeat)
            // The system (no customer) writes the line, "delivered" included so the journey ends with it; a line the tracking rules refuse
            // (order no longer shipped, odd time) is dropped.
            await trackingService.AddEventAsync(target.VendorId, target.ShopOrderId,
                new AddTrackingEventCommand(GhnRules.TrackingStatusOf(status) ?? TrackingRules.Info,
                    null, string.IsNullOrWhiteSpace(webhook.Reason) ? note : $"{note} ({webhook.Reason.Trim()})", at), 0, cancellationToken);

        // "delivered" also moves the shop order; a repeat finds it delivered already and the refused transition changes nothing.
        if (status == "delivered") await orderService.DeliverByCarrierAsync(target.ShopOrderId, note, cancellationToken);
    }

    private async Task ApplyWeighingAsync(GhnShopOrderRef target, GhnWebhook webhook, CancellationToken cancellationToken)
    {
        var measured = Math.Max(webhook.Weight, webhook.ConvertedWeight);
        if (target.ParcelWeightGrams is not { } declared || target.CarrierFee is not { } declaredFee || measured <= declared) return;

        // GHN's new fee when it sends one; otherwise ask GHN what the parcel costs at the weight it found.
        var newFeeVnd = webhook.TotalFee;
        if (newFeeVnd <= 0)
        {
            var destination = await store.GetDestinationAsync(target.OrderId, cancellationToken);
            var pickup = await store.GetPickupAsync(target.VendorId, cancellationToken);
            if (destination?.Place is not { } to || pickup is null) return;
            var insurance = GhnRules.InsuranceOf(await ToVndAsync(target.Subtotal - target.DiscountAmount, cancellationToken), settings);
            // The weight GHN found already includes the box's volume, so the box is left out of the new price.
            var fee = await client.GetFeeAsync(new GhnFeeRequest(pickup.Place, to, new GhnParcel(measured, 1, 1, 1), insurance, settings.ServiceTypeId), cancellationToken);
            if (!fee.Succeeded) return;
            newFeeVnd = fee.Value;
        }

        var owed = GhnRules.WeightAdjustment(declared, measured, declaredFee, await ToPrimaryAsync(newFeeVnd, cancellationToken));
        if (owed <= 0m) return;
        await store.SetShippingAdjustmentAsync(target.ShopOrderId, owed, cancellationToken);
        await auditLog.WriteAsync("shipping.ghn_weight_adjusted", entityType: "ShopOrder", entityId: target.ShopOrderId,
            details: new { shopOrderId = target.ShopOrderId, declared, measured, owed }, cancellationToken: cancellationToken);
    }

    private static DateTime? Parse(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    /// <summary>The parcel of some lines, from the weight and size each product declares.</summary>
    private async Task<GhnParcel> ParcelOfAsync(IEnumerable<(int ProductId, int Quantity)> lines, CancellationToken cancellationToken)
    {
        var list = lines.ToList();
        var sizes = await store.GetProductSizesAsync(list.Select(l => l.ProductId).ToList(), cancellationToken);
        return GhnRules.Parcel(list.Select(l => (l.Quantity, sizes.GetValueOrDefault(l.ProductId))), settings);
    }

    // ---- Money: GHN counts in VND ----

    private async Task<decimal> VndPerPrimaryAsync(CancellationToken cancellationToken)
    {
        var primary = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        if (string.Equals(primary.Code, "VND", StringComparison.OrdinalIgnoreCase)) return 1m;
        var vnd = (await currencyService.GetAllAsync(cancellationToken)).FirstOrDefault(c => string.Equals(c.Code, "VND", StringComparison.OrdinalIgnoreCase));
        return vnd is { RateToPrimary: > 0m } ? vnd.RateToPrimary : settings.VndPerPrimaryUnit;
    }

    private async Task<decimal> ToPrimaryAsync(long vnd, CancellationToken cancellationToken)
    {
        var primary = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        return CurrencyRules.Round(vnd / await VndPerPrimaryAsync(cancellationToken), primary.DecimalPlaces);
    }

    private async Task<long> ToVndAsync(decimal amount, CancellationToken cancellationToken) =>
        (long)Math.Round(amount * await VndPerPrimaryAsync(cancellationToken), 0, MidpointRounding.AwayFromZero);
}
