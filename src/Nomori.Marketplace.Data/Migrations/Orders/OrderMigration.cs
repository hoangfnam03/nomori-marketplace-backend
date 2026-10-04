using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Orders;

/// <summary>
/// F18-A: orders split per shop (customer-orders-prd.md, vendor-orders-prd.md). "Order" is a reserved word, so the order
/// table is <c>CustomerOrder</c>. Lines and addresses are copies taken when the order is placed.
/// </summary>
[Migration(202610160001)]
public sealed class OrderMigration : Migration
{
    public override void Up()
    {
        Create.Table("CustomerOrder")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("OrderNumber").AsString(20).NotNullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("CurrencyCode").AsString(3).NotNullable()
            .WithColumn("ItemsTotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("ShippingTotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("Total").AsDecimal(18, 4).NotNullable()
            .WithColumn("PaymentMethod").AsInt32().NotNullable()
            // Same lengths as CustomerAddress, so a copy always fits.
            .WithColumn("ShipFirstName").AsString(100).NotNullable()
            .WithColumn("ShipLastName").AsString(100).NotNullable()
            .WithColumn("ShipCompany").AsString(100).Nullable()
            .WithColumn("ShipAddress1").AsString(200).NotNullable()
            .WithColumn("ShipAddress2").AsString(200).Nullable()
            .WithColumn("ShipCity").AsString(100).NotNullable()
            .WithColumn("ShipStateProvince").AsString(100).Nullable()
            .WithColumn("ShipCountryCode").AsString(2).NotNullable()
            .WithColumn("ShipZipPostalCode").AsString(20).Nullable()
            .WithColumn("ShipPhoneNumber").AsString(32).NotNullable()
            .WithColumn("IdempotencyKey").AsString(100).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("UX_CustomerOrder_OrderNumber").OnTable("CustomerOrder").OnColumn("OrderNumber").Unique();
        // A retried request finds the first order instead of creating a second one.
        Create.Index("UX_CustomerOrder_Customer_IdempotencyKey").OnTable("CustomerOrder")
            .OnColumn("CustomerId").Ascending().OnColumn("IdempotencyKey").Ascending().WithOptions().Unique();
        Create.Index("IX_CustomerOrder_Customer_Created").OnTable("CustomerOrder")
            .OnColumn("CustomerId").Ascending().OnColumn("CreatedOnUtc").Descending();
        Create.ForeignKey("FK_CustomerOrder_Customer")
            .FromTable("CustomerOrder").ForeignColumn("CustomerId").ToTable("Customer").PrimaryColumn("Id");

        Create.Table("StoreOrder")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("OrderId").AsInt32().NotNullable()
            .WithColumn("VendorId").AsInt32().NotNullable()
            .WithColumn("SubOrderNumber").AsString(24).NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("PaymentStatus").AsInt32().NotNullable()
            .WithColumn("ItemsTotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("ShippingFee").AsDecimal(18, 4).NotNullable()
            .WithColumn("Total").AsDecimal(18, 4).NotNullable()
            .WithColumn("CustomerNote").AsString(500).Nullable()
            .WithColumn("Carrier").AsString(100).Nullable()
            .WithColumn("TrackingNumber").AsString(100).Nullable()
            .WithColumn("ConfirmByUtc").AsDateTime2().NotNullable()
            .WithColumn("ConfirmedOnUtc").AsDateTime2().Nullable()
            .WithColumn("ShippedOnUtc").AsDateTime2().Nullable()
            .WithColumn("DeliveredOnUtc").AsDateTime2().Nullable()
            .WithColumn("CompletedOnUtc").AsDateTime2().Nullable()
            .WithColumn("CancelledOnUtc").AsDateTime2().Nullable()
            .WithColumn("CancelReason").AsString(50).Nullable()
            .WithColumn("CancelNote").AsString(500).Nullable()
            .WithColumn("CancelledBy").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("UX_StoreOrder_SubOrderNumber").OnTable("StoreOrder").OnColumn("SubOrderNumber").Unique();
        Create.Index("IX_StoreOrder_OrderId").OnTable("StoreOrder").OnColumn("OrderId").Ascending();
        // The shop's order list (vendor-orders) and the scheduled jobs filter by shop and status, or by status and deadline.
        Create.Index("IX_StoreOrder_Vendor_Status").OnTable("StoreOrder")
            .OnColumn("VendorId").Ascending().OnColumn("Status").Ascending();
        Create.Index("IX_StoreOrder_Status_ConfirmBy").OnTable("StoreOrder")
            .OnColumn("Status").Ascending().OnColumn("ConfirmByUtc").Ascending();
        Create.ForeignKey("FK_StoreOrder_CustomerOrder")
            .FromTable("StoreOrder").ForeignColumn("OrderId").ToTable("CustomerOrder").PrimaryColumn("Id");
        Create.ForeignKey("FK_StoreOrder_Vendor")
            .FromTable("StoreOrder").ForeignColumn("VendorId").ToTable("Vendor").PrimaryColumn("Id");

        Create.Table("OrderItem")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("StoreOrderId").AsInt32().NotNullable()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("CombinationId").AsInt32().Nullable()
            .WithColumn("ValueIds").AsString(200).NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("ProductName").AsString(400).NotNullable()
            .WithColumn("VariantDescription").AsString(400).Nullable()
            .WithColumn("Sku").AsString(400).Nullable()
            .WithColumn("PictureId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("UnitPrice").AsDecimal(18, 4).NotNullable()
            .WithColumn("Quantity").AsInt32().NotNullable()
            .WithColumn("LineTotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("StockDeducted").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Index("IX_OrderItem_StoreOrderId").OnTable("OrderItem").OnColumn("StoreOrderId").Ascending();
        Create.Index("IX_OrderItem_ProductId").OnTable("OrderItem").OnColumn("ProductId").Ascending();
        Create.ForeignKey("FK_OrderItem_StoreOrder")
            .FromTable("OrderItem").ForeignColumn("StoreOrderId").ToTable("StoreOrder").PrimaryColumn("Id");
        // Products are soft-deleted, so the key never blocks a deletion; it keeps lines pointing at real products.
        Create.ForeignKey("FK_OrderItem_Product")
            .FromTable("OrderItem").ForeignColumn("ProductId").ToTable("Product").PrimaryColumn("Id");

        Create.Table("StoreOrderEvent")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("StoreOrderId").AsInt32().NotNullable()
            .WithColumn("FromStatus").AsInt32().Nullable()
            .WithColumn("ToStatus").AsInt32().NotNullable()
            .WithColumn("ActorType").AsInt32().NotNullable()
            .WithColumn("ActorCustomerId").AsInt32().Nullable()
            .WithColumn("Reason").AsString(50).Nullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("IX_StoreOrderEvent_StoreOrderId").OnTable("StoreOrderEvent").OnColumn("StoreOrderId").Ascending();
        Create.ForeignKey("FK_StoreOrderEvent_StoreOrder")
            .FromTable("StoreOrderEvent").ForeignColumn("StoreOrderId").ToTable("StoreOrder").PrimaryColumn("Id");

        // One row per UTC day; the order number takes the next value under an update lock.
        Create.Table("OrderNumberSequence")
            .WithColumn("Day").AsDate().PrimaryKey()
            .WithColumn("LastValue").AsInt32().NotNullable();
    }

    public override void Down()
    {
        Delete.Table("OrderNumberSequence");
        Delete.Table("StoreOrderEvent");
        Delete.Table("OrderItem");
        Delete.Table("StoreOrder");
        Delete.Table("CustomerOrder");
    }
}
