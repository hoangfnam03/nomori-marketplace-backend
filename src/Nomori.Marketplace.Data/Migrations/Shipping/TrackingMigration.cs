using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Shipping;

/// <summary>F20-B: the carriers shops can pick, and the journey of a parcel as tracking events.</summary>
[Migration(202611050001)]
public sealed class TrackingMigration : Migration
{
    public override void Up()
    {
        Create.Table("Carrier")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(100).NotNullable()
            .WithColumn("TrackingUrlTemplate").AsString(500).Nullable()
            .WithColumn("Active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();
        Create.Index("UX_Carrier_Name").OnTable("Carrier").OnColumn("Name").Ascending().WithOptions().Unique();

        // Only the three carriers whose public tracking address is well known get a link; an administrator can add the others.
        Execute.Sql("""
            INSERT INTO Carrier (Name, TrackingUrlTemplate, Active, DisplayOrder, CreatedOnUtc, UpdatedOnUtc) VALUES
                (N'DHL', N'https://www.dhl.com/global-en/home/tracking/tracking-express.html?submit=1&tracking-id={tracking}', 1, 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (N'FedEx', N'https://www.fedex.com/fedextrack/?trknbr={tracking}', 1, 2, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (N'UPS', N'https://www.ups.com/track?tracknum={tracking}', 1, 3, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (N'Viettel Post', NULL, 1, 4, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (N'Giao Hang Nhanh', NULL, 1, 5, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (N'Giao Hang Tiet Kiem', NULL, 1, 6, SYSUTCDATETIME(), SYSUTCDATETIME());
            """);

        Create.Table("ShopOrderTrackingEvent")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("ShopOrderId").AsInt32().NotNullable()
            .WithColumn("Status").AsString(30).NotNullable()
            .WithColumn("Location").AsString(100).Nullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("OccurredOnUtc").AsDateTime2().NotNullable()
            .WithColumn("CreatedByCustomerId").AsInt32().NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_ShopOrderTrackingEvent_ShopOrder").FromTable("ShopOrderTrackingEvent").ForeignColumn("ShopOrderId").ToTable("ShopOrder").PrimaryColumn("Id");
        Create.Index("IX_ShopOrderTrackingEvent_ShopOrder").OnTable("ShopOrderTrackingEvent")
            .OnColumn("ShopOrderId").Ascending().OnColumn("OccurredOnUtc").Descending();
    }

    public override void Down()
    {
        Delete.Table("ShopOrderTrackingEvent");
        Delete.Table("Carrier");
    }
}
