using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Shipping;

/// <summary>GHN prototype: where GHN picks up each shop's parcels. A shop with no row is not offered GHN.</summary>
[Migration(202611150002)]
public sealed class GhnPickupMigration : Migration
{
    public override void Up()
    {
        Create.Table("VendorGhnPickup")
            .WithColumn("VendorId").AsInt32().PrimaryKey()
            .WithColumn("ContactName").AsString(100).NotNullable()
            .WithColumn("Phone").AsString(20).NotNullable()
            .WithColumn("Address").AsString(200).NotNullable()
            .WithColumn("ProvinceId").AsInt32().NotNullable()
            .WithColumn("ProvinceName").AsString(100).NotNullable()
            .WithColumn("DistrictId").AsInt32().NotNullable()
            .WithColumn("DistrictName").AsString(100).NotNullable()
            .WithColumn("WardCode").AsString(20).NotNullable()
            .WithColumn("WardName").AsString(100).NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_VendorGhnPickup_Vendor").FromTable("VendorGhnPickup").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");
    }

    public override void Down() => Delete.Table("VendorGhnPickup");
}
