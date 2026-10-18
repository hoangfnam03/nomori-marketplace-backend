using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Shipping;

/// <summary>
/// Shipping is GHN only: the shops' own rates (F20-A) go, a shop can offer free shipping over a subtotal, and a shop order records the
/// shipping the shop pays for so settlement can take it off.
/// </summary>
[Migration(202611200001)]
public sealed class GhnOnlyMigration : Migration
{
    public override void Up()
    {
        Delete.Table("ShippingRate");

        Alter.Table("VendorGhnPickup").AddColumn("FreeShippingOverSubtotal").AsDecimal(18, 4).Nullable();
        Alter.Table("ShopOrder").AddColumn("ShopShippingCost").AsDecimal(18, 4).NotNullable().WithDefaultValue(0m);
        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_ShopShippingCost CHECK (ShopShippingCost >= 0);");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_ShopShippingCost;");
        Delete.DefaultConstraint().OnTable("ShopOrder").OnColumn("ShopShippingCost");
        Delete.Column("ShopShippingCost").FromTable("ShopOrder");
        Delete.Column("FreeShippingOverSubtotal").FromTable("VendorGhnPickup");
        // The shops' rates are gone for good; Down only brings back an empty table of the same shape.
        Create.Table("ShippingRate")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("VendorId").AsInt32().NotNullable()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("CountryCode").AsString(2).NotNullable()
            .WithColumn("StateProvinceId").AsInt32().Nullable()
            .WithColumn("Fee").AsDecimal(18, 4).NotNullable()
            .WithColumn("FreeOverSubtotal").AsDecimal(18, 4).Nullable()
            .WithColumn("MinDays").AsInt32().Nullable()
            .WithColumn("MaxDays").AsInt32().Nullable()
            .WithColumn("Published").AsBoolean().NotNullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
    }
}
