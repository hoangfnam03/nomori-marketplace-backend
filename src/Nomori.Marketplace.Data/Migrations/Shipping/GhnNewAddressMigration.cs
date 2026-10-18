using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Shipping;

/// <summary>
/// Addresses move to the two-level administrative units in force since 1 July 2025 (province and ward, no district), with GHN's new ids;
/// the old district and ward codes cannot be converted, so saved GHN places are cleared and picked again. A shop order also keeps what GHN
/// really charged, the declared weight, the cash GHN collects and when it was paid over, the expected day, and what the shop owes for an
/// under-declared weight.
/// </summary>
[Migration(202611200004)]
public sealed class GhnNewAddressMigration : Migration
{
    public override void Up()
    {
        Delete.Column("GhnWardCode").Column("GhnDistrictId").FromTable("CustomerAddress");
        Alter.Table("CustomerAddress")
            .AddColumn("GhnProvinceName").AsString(100).Nullable()
            .AddColumn("GhnWardId").AsInt32().Nullable()
            .AddColumn("GhnWardName").AsString(100).Nullable();
        Execute.Sql("UPDATE CustomerAddress SET GhnProvinceId = NULL, GhnArea = NULL;");

        Delete.Column("GhnWardCode").Column("GhnDistrictId").FromTable("CustomerOrder");
        Alter.Table("CustomerOrder")
            .AddColumn("GhnProvinceId").AsInt32().Nullable()
            .AddColumn("GhnProvinceName").AsString(100).Nullable()
            .AddColumn("GhnWardId").AsInt32().Nullable()
            .AddColumn("GhnWardName").AsString(100).Nullable();

        // A pickup address in the old units is useless to GHN now: the shop gives it again.
        Execute.Sql("DELETE FROM VendorGhnPickup;");
        Delete.Column("WardCode").Column("DistrictName").Column("DistrictId").FromTable("VendorGhnPickup");
        Alter.Table("VendorGhnPickup").AddColumn("WardId").AsInt32().NotNullable();

        Alter.Table("ShopOrder")
            .AddColumn("CarrierFee").AsDecimal(18, 4).Nullable()
            .AddColumn("ParcelWeightGrams").AsInt32().Nullable()
            .AddColumn("ExpectedDeliveryUtc").AsDateTime2().Nullable()
            .AddColumn("CodAmount").AsDecimal(18, 4).NotNullable().WithDefaultValue(0m)
            .AddColumn("CodTransferredOnUtc").AsDateTime2().Nullable()
            .AddColumn("ShippingAdjustment").AsDecimal(18, 4).NotNullable().WithDefaultValue(0m);
        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Shipment CHECK (ShippingAdjustment >= 0 AND CodAmount >= 0 AND (CarrierFee IS NULL OR CarrierFee >= 0));");
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE ShopOrder DROP CONSTRAINT CK_ShopOrder_Shipment;");
        Delete.DefaultConstraint().OnTable("ShopOrder").OnColumn("CodAmount");
        Delete.DefaultConstraint().OnTable("ShopOrder").OnColumn("ShippingAdjustment");
        Delete.Column("ShippingAdjustment").Column("CodTransferredOnUtc").Column("CodAmount").Column("ExpectedDeliveryUtc").Column("ParcelWeightGrams").Column("CarrierFee")
            .FromTable("ShopOrder");

        Execute.Sql("DELETE FROM VendorGhnPickup;");
        Delete.Column("WardId").FromTable("VendorGhnPickup");
        Alter.Table("VendorGhnPickup")
            .AddColumn("DistrictId").AsInt32().NotNullable()
            .AddColumn("DistrictName").AsString(100).NotNullable()
            .AddColumn("WardCode").AsString(20).NotNullable();

        Delete.Column("GhnWardName").Column("GhnWardId").Column("GhnProvinceName").Column("GhnProvinceId").FromTable("CustomerOrder");
        Alter.Table("CustomerOrder").AddColumn("GhnDistrictId").AsInt32().Nullable().AddColumn("GhnWardCode").AsString(20).Nullable();

        Delete.Column("GhnWardName").Column("GhnWardId").Column("GhnProvinceName").FromTable("CustomerAddress");
        Alter.Table("CustomerAddress").AddColumn("GhnDistrictId").AsInt32().Nullable().AddColumn("GhnWardCode").AsString(20).Nullable();
    }
}
