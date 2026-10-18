using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Directory;

/// <summary>
/// The marketplace serves Vietnam: the primary currency becomes the Vietnamese dong (no decimals) instead of the US dollar, which stays as a
/// display currency. Every amount stored in dollars is converted once, at 25,000 VND to the dollar (or the rate of a VND currency already
/// set up), so prices, orders, payments, discounts and the settlement ledger keep meaning the same. Dollar amounts have at most 2 decimals,
/// so every converted amount is a whole number of dong and the totals constraints still hold. Runs only while the dollar is primary.
/// </summary>
[Migration(202611200002)]
public sealed class VndPrimaryMigration : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            IF EXISTS (SELECT 1 FROM Currency WHERE IsPrimary = 1 AND Code = 'USD')
            BEGIN
                DECLARE @Rate decimal(18, 4) = COALESCE((SELECT RateToPrimary FROM Currency WHERE Code = 'VND'), 25000);
                DECLARE @Now datetime2 = SYSUTCDATETIME();

                IF NOT EXISTS (SELECT 1 FROM Currency WHERE Code = 'VND')
                    INSERT INTO Currency (Code, Name, Symbol, DecimalPlaces, RateToPrimary, IsPrimary, Published, DisplayOrder, RateUpdatedOnUtc, CreatedOnUtc, UpdatedOnUtc)
                    VALUES ('VND', N'Việt Nam Đồng', N'₫', 0, @Rate, 0, 1, 0, @Now, @Now, @Now);

                -- Rates are units per primary unit: every rate is rebased on the dong, and only one currency may be primary at a time.
                UPDATE Currency SET RateToPrimary = CASE WHEN Code = 'VND' THEN 1 ELSE ROUND(RateToPrimary / @Rate, 8) END,
                    IsPrimary = 0, RateUpdatedOnUtc = @Now, UpdatedOnUtc = @Now;
                UPDATE Currency SET IsPrimary = 1, DecimalPlaces = 0, Published = 1, DisplayOrder = 0 WHERE Code = 'VND';

                -- Catalog and cart.
                UPDATE Product SET Price = Price * @Rate, OldPrice = OldPrice * @Rate, SpecialPrice = SpecialPrice * @Rate;
                UPDATE ProductTierPrice SET Price = Price * @Rate;
                UPDATE ProductAttributeValue SET PriceAdjustment = PriceAdjustment * @Rate;
                UPDATE ProductAttributeCombination SET OverriddenPrice = OverriddenPrice * @Rate;
                UPDATE CartItem SET AddedUnitPrice = AddedUnitPrice * @Rate;

                -- Discounts: a percentage stays a percentage; amounts are converted.
                UPDATE Discount SET Value = CASE WHEN Type = 1 THEN Value * @Rate ELSE Value END,
                    MaxDiscountAmount = MaxDiscountAmount * @Rate, MinSubtotal = MinSubtotal * @Rate;
                UPDATE DiscountUsage SET Amount = Amount * @Rate;

                -- Orders, payments and returns.
                UPDATE CustomerOrder SET Subtotal = Subtotal * @Rate, ShippingTotal = ShippingTotal * @Rate, DiscountTotal = DiscountTotal * @Rate,
                    TaxTotal = TaxTotal * @Rate, Total = Total * @Rate, CurrencyCode = 'VND'
                WHERE CurrencyCode = 'USD';
                UPDATE ShopOrder SET Subtotal = Subtotal * @Rate, ShippingFee = ShippingFee * @Rate, DiscountAmount = DiscountAmount * @Rate,
                    TaxAmount = TaxAmount * @Rate, Total = Total * @Rate, ShopShippingCost = ShopShippingCost * @Rate
                WHERE OrderId IN (SELECT Id FROM CustomerOrder WHERE CurrencyCode = 'VND');
                UPDATE l SET UnitPrice = l.UnitPrice * @Rate, LineTotal = l.LineTotal * @Rate, TaxAmount = l.TaxAmount * @Rate
                FROM OrderLine l INNER JOIN ShopOrder s ON s.Id = l.ShopOrderId INNER JOIN CustomerOrder o ON o.Id = s.OrderId
                WHERE o.CurrencyCode = 'VND';
                UPDATE PaymentTransaction SET Amount = Amount * @Rate, RefundedAmount = RefundedAmount * @Rate, CurrencyCode = 'VND' WHERE CurrencyCode = 'USD';
                UPDATE ReturnRequest SET RefundAmount = RefundAmount * @Rate, CurrencyCode = 'VND' WHERE CurrencyCode = 'USD';
                UPDATE rl SET Amount = rl.Amount * @Rate
                FROM ReturnRequestLine rl INNER JOIN ReturnRequest r ON r.Id = rl.ReturnRequestId
                WHERE r.CurrencyCode = 'VND';

                -- Settlement ledger and payouts; a shop's free shipping threshold.
                UPDATE SettlementEntry SET Gross = Gross * @Rate, Commission = Commission * @Rate, Amount = Amount * @Rate, CurrencyCode = 'VND' WHERE CurrencyCode = 'USD';
                UPDATE SettlementPayout SET Amount = Amount * @Rate, CurrencyCode = 'VND' WHERE CurrencyCode = 'USD';
                UPDATE VendorGhnPickup SET FreeShippingOverSubtotal = FreeShippingOverSubtotal * @Rate;
            END
            """);
    }

    public override void Down()
    {
        // Converting back would round dong into cents and could break the totals constraints; this migration is not reversed.
    }
}
