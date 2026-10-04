using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Discounts;

/// <summary>F15-A: discount codes, their usage, the discount on orders and shop orders, and the permission to manage platform discounts.</summary>
[Migration(202610190001)]
public sealed class DiscountMigration : Migration
{
    public override void Up()
    {
        Create.Table("Discount")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("VendorId").AsInt32().Nullable()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("Code").AsString(32).NotNullable()
            .WithColumn("Type").AsInt32().NotNullable()
            .WithColumn("Value").AsDecimal(18, 4).NotNullable()
            .WithColumn("MaxDiscountAmount").AsDecimal(18, 4).Nullable()
            .WithColumn("StartsOnUtc").AsDateTime2().Nullable()
            .WithColumn("EndsOnUtc").AsDateTime2().Nullable()
            .WithColumn("MinSubtotal").AsDecimal(18, 4).Nullable()
            .WithColumn("MaxUses").AsInt32().Nullable()
            .WithColumn("MaxUsesPerCustomer").AsInt32().Nullable()
            .WithColumn("UsedCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        // Shops are soft-deleted, so a shop's discounts simply stay (and stop matching, because the shop is no longer in a cart).
        Create.ForeignKey("FK_Discount_Vendor").FromTable("Discount").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");

        Execute.Sql("ALTER TABLE Discount ADD CONSTRAINT CK_Discount_Type CHECK (Type IN (0, 1));");
        Execute.Sql("ALTER TABLE Discount ADD CONSTRAINT CK_Discount_Value CHECK (Value > 0 AND (Type <> 0 OR Value <= 100));");
        Execute.Sql("ALTER TABLE Discount ADD CONSTRAINT CK_Discount_Dates CHECK (StartsOnUtc IS NULL OR EndsOnUtc IS NULL OR EndsOnUtc > StartsOnUtc);");
        Execute.Sql("ALTER TABLE Discount ADD CONSTRAINT CK_Discount_Limits CHECK ((MaxUses IS NULL OR MaxUses > 0) AND (MaxUsesPerCustomer IS NULL OR MaxUsesPerCustomer > 0) AND UsedCount >= 0);");
        Execute.Sql("ALTER TABLE Discount ADD CONSTRAINT CK_Discount_Amounts CHECK ((MaxDiscountAmount IS NULL OR MaxDiscountAmount > 0) AND (MinSubtotal IS NULL OR MinSubtotal > 0));");

        // The code is unique across the platform, whoever owns the discount.
        Create.Index("UX_Discount_Code").OnTable("Discount").OnColumn("Code").Ascending().WithOptions().Unique();
        Create.Index("IX_Discount_Vendor").OnTable("Discount").OnColumn("VendorId").Ascending().OnColumn("Id").Ascending();

        Create.Table("DiscountUsage")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("DiscountId").AsInt32().NotNullable()
            .WithColumn("OrderId").AsInt32().Nullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("Amount").AsDecimal(18, 4).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_DiscountUsage_Discount").FromTable("DiscountUsage").ForeignColumn("DiscountId").ToTable("Discount").PrimaryColumn("Id");
        Create.ForeignKey("FK_DiscountUsage_Order").FromTable("DiscountUsage").ForeignColumn("OrderId")
            .ToTable("CustomerOrder").PrimaryColumn("Id").OnDelete(Rule.SetNull);
        Create.ForeignKey("FK_DiscountUsage_Customer").FromTable("DiscountUsage").ForeignColumn("CustomerId").ToTable("Customer").PrimaryColumn("Id");
        Create.Index("IX_DiscountUsage_Discount_Customer").OnTable("DiscountUsage").OnColumn("DiscountId").Ascending().OnColumn("CustomerId").Ascending();
        // One code per order.
        Execute.Sql("CREATE UNIQUE INDEX UX_DiscountUsage_Order ON DiscountUsage (OrderId) WHERE OrderId IS NOT NULL;");

        // The discount on orders. Existing rows get 0, so the new checks hold for them.
        Alter.Table("CustomerOrder")
            .AddColumn("DiscountCode").AsString(32).Nullable()
            .AddColumn("DiscountTotal").AsDecimal(18, 4).NotNullable().WithDefaultValue(0);
        Alter.Table("ShopOrder")
            .AddColumn("DiscountAmount").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .AddColumn("DiscountFunding").AsString(10).Nullable();

        Execute.Sql("ALTER TABLE CustomerOrder DROP CONSTRAINT CK_CustomerOrder_Amounts;");
        Execute.Sql("""
            ALTER TABLE CustomerOrder ADD CONSTRAINT CK_CustomerOrder_Amounts CHECK (
                Subtotal >= 0 AND ShippingTotal >= 0 AND DiscountTotal >= 0 AND DiscountTotal <= Subtotal
                AND Total = Subtotal - DiscountTotal + ShippingTotal);
            """);
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Amounts;");
        Execute.Sql("""
            ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Amounts CHECK (
                Subtotal >= 0 AND ShippingFee >= 0 AND DiscountAmount >= 0 AND DiscountAmount <= Subtotal
                AND Total = Subtotal - DiscountAmount + ShippingFee);
            """);
        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_DiscountFunding CHECK (DiscountFunding IS NULL OR DiscountFunding IN ('platform', 'shop'));");

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'discounts.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('discounts.manage', 'Manage platform discounts', 'Discounts');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'discounts.manage'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN PermissionRecord p ON p.Id = m.PermissionRecordId
            WHERE p.SystemName = 'discounts.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'discounts.manage';
            """);

        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_DiscountFunding;");
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Amounts;");
        Execute.Sql("ALTER TABLE CustomerOrder DROP CONSTRAINT CK_CustomerOrder_Amounts;");
        // The columns carry default constraints; they go with the columns.
        Delete.Column("DiscountAmount").FromTable("ShopOrder");
        Delete.Column("DiscountFunding").FromTable("ShopOrder");
        Delete.Column("DiscountCode").FromTable("CustomerOrder");
        Delete.Column("DiscountTotal").FromTable("CustomerOrder");
        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Amounts CHECK (Subtotal >= 0 AND ShippingFee >= 0 AND Total = Subtotal + ShippingFee);");
        Execute.Sql("ALTER TABLE CustomerOrder ADD CONSTRAINT CK_CustomerOrder_Amounts CHECK (Subtotal >= 0 AND ShippingTotal >= 0 AND Total = Subtotal + ShippingTotal);");

        Delete.Table("DiscountUsage");
        Delete.Table("Discount");
    }
}
