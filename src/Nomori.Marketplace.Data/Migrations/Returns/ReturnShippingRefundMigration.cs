using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Returns;

/// <summary>
/// The shipping the customer paid is given back when a shop order is returned whole: the return that completes it carries the amount and who
/// bears it (the shop when its goods were at fault, otherwise the platform).
/// </summary>
[Migration(202611210008)]
public sealed class ReturnShippingRefundMigration : Migration
{
    public override void Up()
    {
        Alter.Table("ReturnRequest")
            .AddColumn("ShippingRefund").AsDecimal(18, 4).NotNullable().WithDefaultValue(0)
            .AddColumn("ShippingRefundPaidBy").AsString(10).Nullable();
        Execute.Sql("""
            ALTER TABLE ReturnRequest ADD CONSTRAINT CK_ReturnRequest_ShippingRefund
                CHECK (ShippingRefund >= 0 AND (ShippingRefundPaidBy IS NULL OR ShippingRefundPaidBy IN ('shop', 'platform')));
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE ReturnRequest DROP CONSTRAINT CK_ReturnRequest_ShippingRefund;");
        Delete.DefaultConstraint().OnTable("ReturnRequest").OnColumn("ShippingRefund");
        Delete.Column("ShippingRefundPaidBy").Column("ShippingRefund").FromTable("ReturnRequest");
    }
}
