using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Returns;

/// <summary>F21-A: return requests on shop orders and the items they concern.</summary>
[Migration(202610290001)]
public sealed class ReturnMigration : Migration
{
    public override void Up()
    {
        Create.Table("ReturnRequest")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            // The real number needs the id; the store fills it in after the insert.
            .WithColumn("Number").AsString(40).NotNullable()
            .WithColumn("ShopOrderId").AsInt32().NotNullable()
            .WithColumn("OrderId").AsInt32().NotNullable()
            .WithColumn("ShopOrderNumber").AsString(50).NotNullable()
            .WithColumn("VendorId").AsInt32().NotNullable()
            .WithColumn("ShopName").AsString(200).NotNullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            // 0 requested, 1 approved, 2 rejected, 3 received, 4 refunded, 5 withdrawn
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("Reason").AsString(30).NotNullable()
            .WithColumn("CustomerNote").AsString(500).Nullable()
            .WithColumn("ResolutionNote").AsString(500).Nullable()
            .WithColumn("CurrencyCode").AsString(3).NotNullable()
            .WithColumn("RefundAmount").AsDecimal(18, 4).NotNullable()
            .WithColumn("Restocked").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("PaymentId").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_ReturnRequest_ShopOrder").FromTable("ReturnRequest").ForeignColumn("ShopOrderId").ToTable("ShopOrder").PrimaryColumn("Id");
        Execute.Sql("ALTER TABLE ReturnRequest ADD CONSTRAINT CK_ReturnRequest_Status CHECK (Status BETWEEN 0 AND 5);");
        Execute.Sql("ALTER TABLE ReturnRequest ADD CONSTRAINT CK_ReturnRequest_Amount CHECK (RefundAmount >= 0);");

        Create.Index("UX_ReturnRequest_Number").OnTable("ReturnRequest").OnColumn("Number").Ascending().WithOptions().Unique();
        Create.Index("IX_ReturnRequest_Customer").OnTable("ReturnRequest").OnColumn("CustomerId").Ascending().OnColumn("Id").Descending();
        Create.Index("IX_ReturnRequest_Vendor_Status").OnTable("ReturnRequest")
            .OnColumn("VendorId").Ascending().OnColumn("Status").Ascending().OnColumn("Id").Descending();
        Create.Index("IX_ReturnRequest_ShopOrder").OnTable("ReturnRequest").OnColumn("ShopOrderId").Ascending();

        Create.Table("ReturnRequestLine")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ReturnRequestId").AsInt32().NotNullable()
            .WithColumn("OrderLineId").AsInt32().NotNullable()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("VariantLabel").AsString(200).Nullable()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("CombinationId").AsInt32().Nullable()
            .WithColumn("Quantity").AsInt32().NotNullable()
            .WithColumn("Amount").AsDecimal(18, 4).NotNullable();

        Create.ForeignKey("FK_ReturnRequestLine_ReturnRequest").FromTable("ReturnRequestLine").ForeignColumn("ReturnRequestId")
            .ToTable("ReturnRequest").PrimaryColumn("Id").OnDelete(System.Data.Rule.Cascade);
        Execute.Sql("ALTER TABLE ReturnRequestLine ADD CONSTRAINT CK_ReturnRequestLine_Quantity CHECK (Quantity >= 1);");
        Create.Index("IX_ReturnRequestLine_Request").OnTable("ReturnRequestLine").OnColumn("ReturnRequestId").Ascending();
        Create.Index("IX_ReturnRequestLine_OrderLine").OnTable("ReturnRequestLine").OnColumn("OrderLineId").Ascending();
    }

    public override void Down()
    {
        Delete.Table("ReturnRequestLine");
        Delete.Table("ReturnRequest");
    }
}
