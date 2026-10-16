using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Shipping;

/// <summary>
/// GHN prototype: an address can carry GHN's province, district and ward (the carrier needs them to quote and deliver), and an order keeps a copy
/// of the district and ward it was placed for, like the rest of the recipient.
/// </summary>
[Migration(202611150001)]
public sealed class GhnMigration : Migration
{
    public override void Up()
    {
        Alter.Table("CustomerAddress")
            .AddColumn("GhnProvinceId").AsInt32().Nullable()
            .AddColumn("GhnDistrictId").AsInt32().Nullable()
            .AddColumn("GhnWardCode").AsString(20).Nullable()
            .AddColumn("GhnArea").AsString(300).Nullable();

        Alter.Table("CustomerOrder")
            .AddColumn("GhnDistrictId").AsInt32().Nullable()
            .AddColumn("GhnWardCode").AsString(20).Nullable()
            .AddColumn("GhnArea").AsString(300).Nullable();
    }

    public override void Down()
    {
        Delete.Column("GhnArea").Column("GhnWardCode").Column("GhnDistrictId").FromTable("CustomerOrder");
        Delete.Column("GhnArea").Column("GhnWardCode").Column("GhnDistrictId").Column("GhnProvinceId").FromTable("CustomerAddress");
    }
}
