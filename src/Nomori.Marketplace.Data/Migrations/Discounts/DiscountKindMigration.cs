using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Discounts;

/// <summary>
/// Shipping codes beside item codes: a discount takes off the items (0) or the shipping (1), and an order can use one code of each kind.
/// A shop order's ShippingFee stays what the customer pays for shipping; the shipping code's part is kept beside it.
/// </summary>
[Migration(202611210001)]
public sealed class DiscountKindMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Discount").AddColumn("Kind").AsInt32().NotNullable().WithDefaultValue(0);
        Execute.Sql("ALTER TABLE Discount ADD CONSTRAINT CK_Discount_Kind CHECK (Kind IN (0, 1));");

        // One use per order becomes one use per order and kind.
        Alter.Table("DiscountUsage").AddColumn("Kind").AsInt32().NotNullable().WithDefaultValue(0);
        Execute.Sql("""
            DROP INDEX UX_DiscountUsage_Order ON DiscountUsage;
            CREATE UNIQUE INDEX UX_DiscountUsage_Order ON DiscountUsage (OrderId, Kind) WHERE OrderId IS NOT NULL;
            """);

        Alter.Table("CustomerOrder")
            .AddColumn("ShippingDiscountCode").AsString(32).Nullable()
            .AddColumn("ShippingDiscountTotal").AsDecimal(18, 4).NotNullable().WithDefaultValue(0);
        Alter.Table("ShopOrder")
            .AddColumn("ShippingDiscount").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .AddColumn("ShippingDiscountFunding").AsString(10).Nullable();
        Execute.Sql("""
            ALTER TABLE CustomerOrder ADD CONSTRAINT CK_CustomerOrder_ShippingDiscount CHECK (ShippingDiscountTotal >= 0);
            ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_ShippingDiscount CHECK (
                ShippingDiscount >= 0 AND (ShippingDiscountFunding IS NULL OR ShippingDiscountFunding IN ('platform', 'shop')));
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_ShippingDiscount;
            ALTER TABLE CustomerOrder DROP CONSTRAINT CK_CustomerOrder_ShippingDiscount;
            """);
        // The columns carry default constraints; they go with the columns.
        Delete.Column("ShippingDiscountFunding").Column("ShippingDiscount").FromTable("ShopOrder");
        Delete.Column("ShippingDiscountTotal").Column("ShippingDiscountCode").FromTable("CustomerOrder");

        // Shipping uses cannot fit the old one-use-per-order index; they are dropped with their kind.
        Execute.Sql("""
            UPDATE d SET UsedCount = d.UsedCount - u.Uses
            FROM Discount d INNER JOIN (SELECT DiscountId, COUNT(*) AS Uses FROM DiscountUsage WHERE Kind = 1 GROUP BY DiscountId) u ON u.DiscountId = d.Id;
            DELETE FROM DiscountUsage WHERE Kind = 1;
            DROP INDEX UX_DiscountUsage_Order ON DiscountUsage;
            CREATE UNIQUE INDEX UX_DiscountUsage_Order ON DiscountUsage (OrderId) WHERE OrderId IS NOT NULL;
            """);
        Delete.Column("Kind").FromTable("DiscountUsage");

        Execute.Sql("ALTER TABLE Discount DROP CONSTRAINT CK_Discount_Kind;");
        Delete.Column("Kind").FromTable("Discount");
    }
}
