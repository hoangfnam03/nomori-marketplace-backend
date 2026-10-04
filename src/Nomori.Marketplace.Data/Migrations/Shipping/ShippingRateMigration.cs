using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Shipping;

/// <summary>F20-A: flat shipping rates per shop and destination.</summary>
[Migration(202610160001)]
public sealed class ShippingRateMigration : Migration
{
    public override void Up()
    {
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
            .WithColumn("Published").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        // No cascade: shops are soft-deleted, so their rows simply stay.
        Create.ForeignKey("FK_ShippingRate_Vendor")
            .FromTable("ShippingRate").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id");

        Create.ForeignKey("FK_ShippingRate_StateProvince")
            .FromTable("ShippingRate").ForeignColumn("StateProvinceId")
            .ToTable("StateProvince").PrimaryColumn("Id");

        Execute.Sql("ALTER TABLE ShippingRate ADD CONSTRAINT CK_ShippingRate_Fee CHECK (Fee >= 0);");
        Execute.Sql("ALTER TABLE ShippingRate ADD CONSTRAINT CK_ShippingRate_FreeOver CHECK (FreeOverSubtotal IS NULL OR FreeOverSubtotal > 0);");
        Execute.Sql("""
            ALTER TABLE ShippingRate ADD CONSTRAINT CK_ShippingRate_Days CHECK (
                (MinDays IS NULL OR MinDays BETWEEN 0 AND 365)
                AND (MaxDays IS NULL OR MaxDays BETWEEN 0 AND 365)
                AND (MinDays IS NULL OR MaxDays IS NULL OR MinDays <= MaxDays));
            """);

        Create.Index("IX_ShippingRate_Vendor_Country")
            .OnTable("ShippingRate")
            .OnColumn("VendorId").Ascending()
            .OnColumn("CountryCode").Ascending();
    }

    public override void Down() => Delete.Table("ShippingRate");
}
