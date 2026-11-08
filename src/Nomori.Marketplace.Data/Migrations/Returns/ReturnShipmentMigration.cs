using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Returns;

/// <summary>
/// F21 with GHN: the carrier's parcel from the customer back to the shop, made when a return is approved. Its fee is borne by the customer
/// (no longer needed: taken off the refund) or the shop (goods at fault: taken off its settlement).
/// </summary>
[Migration(202611210003)]
public sealed class ReturnShipmentMigration : Migration
{
    public override void Up()
    {
        Alter.Table("ReturnRequest")
            .AddColumn("ReturnTrackingNumber").AsString(50).Nullable()
            .AddColumn("ReturnShippingFee").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .AddColumn("ReturnShippingPaidBy").AsString(10).Nullable()
            .AddColumn("ReturnShipmentStatus").AsString(200).Nullable()
            .AddColumn("ReturnShipmentError").AsString(300).Nullable();
        Execute.Sql("""
            ALTER TABLE ReturnRequest ADD CONSTRAINT CK_ReturnRequest_Shipping CHECK (
                ReturnShippingFee >= 0 AND (ReturnShippingPaidBy IS NULL OR ReturnShippingPaidBy IN ('customer', 'shop')));
            CREATE INDEX IX_ReturnRequest_Tracking ON ReturnRequest (ReturnTrackingNumber) WHERE ReturnTrackingNumber IS NOT NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP INDEX IX_ReturnRequest_Tracking ON ReturnRequest;
            ALTER TABLE ReturnRequest DROP CONSTRAINT CK_ReturnRequest_Shipping;
            """);
        // The columns carry default constraints; they go with the columns.
        Delete.Column("ReturnShipmentError").Column("ReturnShipmentStatus").Column("ReturnShippingPaidBy").Column("ReturnShippingFee")
            .Column("ReturnTrackingNumber").FromTable("ReturnRequest");
    }
}
