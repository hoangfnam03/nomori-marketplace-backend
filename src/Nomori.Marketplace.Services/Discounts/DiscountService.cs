using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Discounts;

public sealed class DiscountService(
    IDiscountStore store,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : IDiscountService
{
    // ---- Scope ----

    public Task<IReadOnlyList<Discount>> GetListAsync(int? vendorId, CancellationToken cancellationToken) =>
        store.GetListAsync(vendorId, cancellationToken);

    public async Task<CatalogResult<Discount>> CreateAsync(int? vendorId, SaveDiscountCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var errors = await ValidateAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<Discount>(errors);

        var limit = vendorId is null ? DiscountLimits.MaxPlatform : DiscountLimits.MaxPerShop;
        if (await store.CountAsync(vendorId, cancellationToken) >= limit) return CatalogResult.Error<Discount>(DiscountErrors.Limit);

        var now = clock.UtcNow;
        var discount = new Discount { VendorId = vendorId, CreatedOnUtc = now };
        Apply(discount, command, now);
        discount.Id = await store.InsertAsync(discount, cancellationToken);
        if (discount.Id == 0) return CatalogResult.Error<Discount>(DiscountErrors.CodeExists);

        await auditLog.WriteAsync("discount.created", actorCustomerId, entityType: "Discount", entityId: discount.Id,
            details: new { discountId = discount.Id, vendorId, discount.Code, type = DiscountRules.ToWire(discount.Type) }, cancellationToken: cancellationToken);
        return CatalogResult.Success(discount);
    }

    public async Task<CatalogResult<Discount>> UpdateAsync(int? vendorId, int id, SaveDiscountCommand command, int actorCustomerId, CancellationToken cancellationToken)
    {
        var discount = await OwnedAsync(vendorId, id, cancellationToken);
        if (discount is null) return CatalogResult.Error<Discount>(CatalogErrors.NotFound);

        var errors = await ValidateAsync(command, cancellationToken);
        if (errors.Count > 0) return CatalogResult.Failure<Discount>(errors);

        Apply(discount, command, clock.UtcNow);
        if (!await store.UpdateAsync(discount, cancellationToken)) return CatalogResult.Error<Discount>(DiscountErrors.CodeExists);

        await auditLog.WriteAsync("discount.updated", actorCustomerId, entityType: "Discount", entityId: id,
            details: new { discountId = id, vendorId, discount.Code, discount.Enabled }, cancellationToken: cancellationToken);
        return CatalogResult.Success(discount);
    }

    public async Task<CatalogResult<bool>> DeleteAsync(int? vendorId, int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var discount = await OwnedAsync(vendorId, id, cancellationToken);
        if (discount is null) return CatalogResult.Error<bool>(CatalogErrors.NotFound);

        // A used discount is part of the history of orders: it can only be switched off.
        if (discount.UsedCount > 0 || await store.HasUsageAsync(id, cancellationToken)) return CatalogResult.Error<bool>(DiscountErrors.InUse);

        await store.DeleteAsync(id, cancellationToken);
        await auditLog.WriteAsync("discount.deleted", actorCustomerId, entityType: "Discount", entityId: id,
            details: new { discountId = id, vendorId, discount.Code }, cancellationToken: cancellationToken);
        return CatalogResult.Success(true);
    }

    // ---- Checkout ----

    public async Task<CouponCheck> CheckCouponAsync(string? code, int customerId, IReadOnlyDictionary<int, decimal> shopSubtotals, CancellationToken cancellationToken)
    {
        var normalized = DiscountRules.NormalizeCode(code);
        if (!DiscountRules.IsValidCode(normalized)) return new CouponCheck(null, DiscountReasons.NotFound);

        var discount = await store.GetByCodeAsync(normalized, cancellationToken);
        if (discount is null) return new CouponCheck(null, DiscountReasons.NotFound);

        var uses = discount.MaxUsesPerCustomer is null ? 0 : await store.CountCustomerUsesAsync(discount.Id, customerId, cancellationToken);
        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        return DiscountRules.Evaluate(discount, uses, clock.UtcNow, shopSubtotals, currency.DecimalPlaces);
    }

    public async Task<RedeemOutcome> RedeemAsync(AppliedDiscount applied, int customerId, int orderId, CancellationToken cancellationToken)
    {
        var outcome = await store.TryRedeemAsync(new RedeemRequest(applied.Discount.Id, customerId, orderId, applied.Amount, clock.UtcNow), cancellationToken);
        if (outcome == RedeemOutcome.Redeemed)
            await auditLog.WriteAsync("discount.redeemed", customerId, entityType: "Discount", entityId: applied.Discount.Id,
                details: new { discountId = applied.Discount.Id, orderId, applied.Amount }, cancellationToken: cancellationToken);
        return outcome;
    }

    public async Task ReleaseAsync(int orderId, CancellationToken cancellationToken)
    {
        if (await store.ReleaseAsync(orderId, cancellationToken))
            await auditLog.WriteAsync("discount.released", entityType: "Order", entityId: orderId, details: new { orderId }, cancellationToken: cancellationToken);
    }

    // ---- Helpers ----

    /// <summary>The discount only when it belongs to this scope; another shop's (or the platform's) does not exist for the caller.</summary>
    private async Task<Discount?> OwnedAsync(int? vendorId, int id, CancellationToken cancellationToken)
    {
        var discount = await store.GetAsync(id, cancellationToken);
        return discount is not null && discount.VendorId == vendorId ? discount : null;
    }

    private async Task<Dictionary<string, string[]>> ValidateAsync(SaveDiscountCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var places = (await primaryCurrency.GetPrimaryAsync(cancellationToken)).DecimalPlaces;

        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > DiscountLimits.MaxNameLength) errors["name"] = [$"Enter a name of 1 to {DiscountLimits.MaxNameLength} characters."];

        var code = DiscountRules.NormalizeCode(command.Code);
        if (!DiscountRules.IsValidCode(code))
            errors["code"] = [$"The code needs {DiscountLimits.MinCodeLength} to {DiscountLimits.MaxCodeLength} letters, digits, '-' or '_'."];

        if (!DiscountRules.TryParseType(command.Type, out var type))
        {
            errors["type"] = ["Choose percentage or fixed."];
        }
        else if (type == DiscountType.Percentage)
        {
            if (command.Value <= 0 || command.Value > 100 || CurrencyRules.DecimalPlacesOf(command.Value) > 2)
                errors["value"] = ["A percentage must be above 0 and at most 100, with at most 2 decimal places."];
            if (command.MaxDiscountAmount is { } cap)
                CheckAmount(errors, "maxDiscountAmount", cap, places);
        }
        else
        {
            CheckAmount(errors, "value", command.Value, places);
            if (command.MaxDiscountAmount is not null) errors["maxDiscountAmount"] = ["A cap only applies to a percentage."];
        }

        if (command.MinSubtotal is { } minimum && (minimum <= 0 || minimum > DiscountLimits.MaxAmount || !CurrencyRules.HasValidScale(minimum, places)))
            errors["minSubtotal"] = [$"The minimum must be above 0 and have at most {places} decimal places."];

        if (command.StartsOnUtc is { } start && command.EndsOnUtc is { } end && end <= start)
            errors["endsOnUtc"] = ["The end has to be after the start."];

        if (command.MaxUses is < 1 or > DiscountLimits.MaxUses) errors["maxUses"] = [$"The limit must be between 1 and {DiscountLimits.MaxUses}."];
        if (command.MaxUsesPerCustomer is < 1 or > DiscountLimits.MaxUses) errors["maxUsesPerCustomer"] = [$"The limit must be between 1 and {DiscountLimits.MaxUses}."];
        return errors;
    }

    private static void CheckAmount(Dictionary<string, string[]> errors, string field, decimal amount, int places)
    {
        if (amount <= 0 || amount > DiscountLimits.MaxAmount || !CurrencyRules.HasValidScale(amount, places))
            errors[field] = [$"The amount must be above 0, with at most {places} decimal places."];
    }

    private static void Apply(Discount discount, SaveDiscountCommand command, DateTime now)
    {
        // Validation has accepted the type already; the default only stands for a type that cannot get here.
        var type = DiscountRules.TryParseType(command.Type, out var parsed) ? parsed : DiscountType.Percentage;
        discount.Name = command.Name!.Trim();
        discount.Code = DiscountRules.NormalizeCode(command.Code);
        discount.Type = type;
        discount.Value = command.Value;
        discount.MaxDiscountAmount = type == DiscountType.Percentage ? command.MaxDiscountAmount : null;
        discount.StartsOnUtc = command.StartsOnUtc;
        discount.EndsOnUtc = command.EndsOnUtc;
        discount.MinSubtotal = command.MinSubtotal;
        discount.MaxUses = command.MaxUses;
        discount.MaxUsesPerCustomer = command.MaxUsesPerCustomer;
        discount.Enabled = command.Enabled;
        discount.UpdatedOnUtc = now;
    }
}
