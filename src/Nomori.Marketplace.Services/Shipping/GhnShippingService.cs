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

    // ---- Address picker ----

    public Task<GhnResult<IReadOnlyList<GhnProvince>>> GetProvincesAsync(CancellationToken cancellationToken) =>
        IsConfigured ? client.GetProvincesAsync(cancellationToken) : Task.FromResult(GhnResult.Fail<IReadOnlyList<GhnProvince>>("GHN is not configured."));

    public Task<GhnResult<IReadOnlyList<GhnDistrict>>> GetDistrictsAsync(int provinceId, CancellationToken cancellationToken) =>
        IsConfigured ? client.GetDistrictsAsync(provinceId, cancellationToken) : Task.FromResult(GhnResult.Fail<IReadOnlyList<GhnDistrict>>("GHN is not configured."));

    public Task<GhnResult<IReadOnlyList<GhnWard>>> GetWardsAsync(int districtId, CancellationToken cancellationToken) =>
        IsConfigured ? client.GetWardsAsync(districtId, cancellationToken) : Task.FromResult(GhnResult.Fail<IReadOnlyList<GhnWard>>("GHN is not configured."));

    // ---- Checkout ----

    public async Task<ShippingOption?> QuoteAsync(int vendorId, int districtId, string wardCode, IReadOnlyList<int> quantities, CancellationToken cancellationToken)
    {
        if (!IsConfigured || quantities.Count == 0) return null;
        // A shop without a pickup address does not ship with GHN.
        if (await store.GetPickupAsync(vendorId, cancellationToken) is not { } pickup) return null;

        var fee = await client.GetFeeAsync(new GhnFeeRequest(
            districtId, wardCode, GhnRules.WeightOf(quantities, settings.ItemWeightGrams),
            settings.ParcelLengthCm, settings.ParcelWidthCm, settings.ParcelHeightCm, settings.ServiceTypeId, pickup.DistrictId, pickup.WardCode), cancellationToken);
        if (!fee.Succeeded) return null;

        var amount = await ToPrimaryAsync(fee.Value, cancellationToken);
        return new ShippingOption(GhnRules.RateId, GhnRules.MethodName, amount, amount == 0m, null, null);
    }

    // ---- The shop's pickup address ----

    public Task<GhnPickup?> GetPickupAsync(int vendorId, CancellationToken cancellationToken) => store.GetPickupAsync(vendorId, cancellationToken);

    public async Task<CatalogResult<GhnPickup>> SavePickupAsync(int vendorId, SaveGhnPickupCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        static string Clean(string? value) => value?.Trim() ?? string.Empty;
        var name = Clean(command.ContactName);
        var phone = new string(Clean(command.Phone).Where(c => !char.IsWhiteSpace(c) && c is not '.' and not '-').ToArray());
        var address = Clean(command.Address);
        var (province, district, ward, wardCode) = (Clean(command.ProvinceName), Clean(command.DistrictName), Clean(command.WardName), Clean(command.WardCode));

        if (name.Length is 0 or > 100) errors["contactName"] = ["Enter the name of the person who hands over the parcels (at most 100 characters)."];
        if (!GhnRules.IsVietnamesePhone(phone)) errors["phone"] = ["Enter a Vietnamese phone number, for example 0901234567."];
        if (address.Length is 0 or > 200) errors["address"] = ["Enter the house number and street (at most 200 characters)."];
        if (command.ProvinceId <= 0 || command.DistrictId <= 0 || wardCode.Length is 0 or > 20
            || province.Length is 0 or > 100 || district.Length is 0 or > 100 || ward.Length is 0 or > 100)
            errors["wardCode"] = ["Choose the province, district and ward."];
        if (errors.Count > 0) return CatalogResult.Failure<GhnPickup>(errors);

        var pickup = new GhnPickup
        {
            VendorId = vendorId, ContactName = name, Phone = phone, Address = address, ProvinceId = command.ProvinceId, ProvinceName = province,
            DistrictId = command.DistrictId, DistrictName = district, WardCode = wardCode, WardName = ward, UpdatedOnUtc = clock.UtcNow
        };
        await store.SavePickupAsync(pickup, cancellationToken);
        await auditLog.WriteAsync("shipping.ghn_pickup_saved", actorCustomerId, entityType: "Vendor", entityId: vendorId,
            details: new { vendorId, pickup.DistrictId, pickup.WardCode }, cancellationToken: cancellationToken);
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
        if (destination?.DistrictId is not { } districtId || string.IsNullOrWhiteSpace(destination.WardCode))
            return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NoArea);
        if (await store.GetPickupAsync(vendorId, cancellationToken) is not { } pickup) return CatalogResult.Error<ShopOrderDetail>(GhnErrors.NoPickup);

        // Cash on delivery: the GHN driver collects what the customer owes for this shop's parcel.
        var cod = string.Equals(destination.PaymentMethod, "cod", StringComparison.OrdinalIgnoreCase) ? await ToVndAsync(shopOrder.Total, cancellationToken) : 0L;
        var items = new List<GhnItem>();
        foreach (var line in shopOrder.Lines)
        {
            var name = line.VariantLabel is null ? line.Name : $"{line.Name} ({line.VariantLabel})";
            items.Add(new GhnItem(name, line.Quantity, settings.ItemWeightGrams, await ToVndAsync(line.UnitPrice, cancellationToken)));
        }

        var address = string.Join(", ", new[] { destination.Address1, destination.Address2, destination.Area, destination.City }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        var created = await client.CreateOrderAsync(new GhnCreateOrderRequest(
            destination.RecipientName, destination.RecipientPhone, address, districtId, destination.WardCode!, cod, null,
            string.Join(", ", items.Select(i => $"{i.Quantity} x {i.Name}")) is { Length: > 0 } content ? content[..Math.Min(content.Length, 2000)] : shopOrder.Number,
            GhnRules.WeightOf(shopOrder.Lines.Select(l => l.Quantity), settings.ItemWeightGrams),
            settings.ParcelLengthCm, settings.ParcelWidthCm, settings.ParcelHeightCm, settings.ServiceTypeId, settings.RequiredNote, shopOrder.Number, items,
            pickup.ToSender()),
            cancellationToken);
        if (!created.Succeeded) return CatalogResult.Failure<ShopOrderDetail>("ghn", $"GHN: {created.Error}");

        return await orderService.ShipAsync(vendorId, shopOrderId, new ShipCommand(GhnRules.CarrierName, created.Value!.OrderCode), actorCustomerId, cancellationToken);
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
        // Only status changes of this platform's GHN shop are read; the other event types (weight, COD, fee) are not used yet.
        if (!string.Equals(webhook.Type, "switch_status", StringComparison.OrdinalIgnoreCase)) return GhnWebhookOutcome.Handled;
        if (webhook.ShopID != settings.ShopId || string.IsNullOrWhiteSpace(webhook.OrderCode) || string.IsNullOrWhiteSpace(webhook.Status))
            return GhnWebhookOutcome.Ignored;

        var target = await store.FindByOrderCodeAsync(webhook.OrderCode.Trim(), cancellationToken);
        if (target is null) return GhnWebhookOutcome.Ignored;

        var status = webhook.Status.Trim().ToLowerInvariant();
        var at = DateTime.TryParse(webhook.Time, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : (DateTime?)null;

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
        return GhnWebhookOutcome.Handled;
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
