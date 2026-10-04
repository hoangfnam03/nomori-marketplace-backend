using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Orders;

/// <summary>F18-A: orders, shop orders, lines with price snapshots, history, and the permission to manage orders.</summary>
[Migration(202610180001)]
public sealed class OrderMigration : Migration
{
    public override void Up()
    {
        // Not called "Order": that is a reserved word.
        Create.Table("CustomerOrder")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Number").AsString(40).NotNullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("PlacementKey").AsString(100).NotNullable()
            .WithColumn("CurrencyCode").AsString(3).NotNullable()
            .WithColumn("Subtotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("ShippingTotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("Total").AsDecimal(18, 4).NotNullable()
            .WithColumn("PaymentMethod").AsString(50).NotNullable()
            .WithColumn("CustomerNote").AsString(500).Nullable()
            .WithColumn("RecipientName").AsString(200).NotNullable()
            .WithColumn("RecipientPhone").AsString(50).NotNullable()
            .WithColumn("Address1").AsString(200).NotNullable()
            .WithColumn("Address2").AsString(200).Nullable()
            .WithColumn("City").AsString(100).NotNullable()
            .WithColumn("StateProvince").AsString(100).Nullable()
            .WithColumn("PostalCode").AsString(20).Nullable()
            .WithColumn("CountryCode").AsString(2).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        // No cascade: an order is a record of money and outlives the account's data.
        Create.ForeignKey("FK_CustomerOrder_Customer")
            .FromTable("CustomerOrder").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        Execute.Sql("ALTER TABLE CustomerOrder ADD CONSTRAINT CK_CustomerOrder_Amounts CHECK (Subtotal >= 0 AND ShippingTotal >= 0 AND Total = Subtotal + ShippingTotal);");
        Create.Index("UX_CustomerOrder_Number").OnTable("CustomerOrder").OnColumn("Number").Ascending().WithOptions().Unique();
        // One order per placement key: this is what makes a retried checkout harmless.
        Create.Index("UX_CustomerOrder_PlacementKey").OnTable("CustomerOrder").OnColumn("PlacementKey").Ascending().WithOptions().Unique();
        Create.Index("IX_CustomerOrder_Customer").OnTable("CustomerOrder").OnColumn("CustomerId").Ascending().OnColumn("Id").Descending();

        Create.Table("ShopOrder")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("OrderId").AsInt32().NotNullable()
            .WithColumn("VendorId").AsInt32().NotNullable()
            .WithColumn("Number").AsString(60).NotNullable()
            .WithColumn("ShopName").AsString(200).NotNullable()
            .WithColumn("Status").AsInt32().NotNullable()
            .WithColumn("Subtotal").AsDecimal(18, 4).NotNullable()
            .WithColumn("ShippingFee").AsDecimal(18, 4).NotNullable()
            .WithColumn("Total").AsDecimal(18, 4).NotNullable()
            .WithColumn("ShippingMethodName").AsString(100).NotNullable()
            .WithColumn("ShippingRateId").AsInt32().Nullable()
            .WithColumn("Carrier").AsString(100).Nullable()
            .WithColumn("TrackingNumber").AsString(100).Nullable()
            .WithColumn("CancelReason").AsString(500).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_ShopOrder_Order")
            .FromTable("ShopOrder").ForeignColumn("OrderId")
            .ToTable("CustomerOrder").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("FK_ShopOrder_Vendor")
            .FromTable("ShopOrder").ForeignColumn("VendorId")
            .ToTable("Vendor").PrimaryColumn("Id");

        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Status CHECK (Status BETWEEN 0 AND 5);");
        Execute.Sql("ALTER TABLE ShopOrder ADD CONSTRAINT CK_ShopOrder_Amounts CHECK (Subtotal >= 0 AND ShippingFee >= 0 AND Total = Subtotal + ShippingFee);");
        Create.Index("UX_ShopOrder_Number").OnTable("ShopOrder").OnColumn("Number").Ascending().WithOptions().Unique();
        // A shop appears once in an order.
        Create.Index("UX_ShopOrder_Order_Vendor").OnTable("ShopOrder")
            .OnColumn("OrderId").Ascending().OnColumn("VendorId").Ascending().WithOptions().Unique();
        Create.Index("IX_ShopOrder_Vendor_Status").OnTable("ShopOrder")
            .OnColumn("VendorId").Ascending().OnColumn("Status").Ascending().OnColumn("Id").Descending();

        Create.Table("OrderLine")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ShopOrderId").AsInt32().NotNullable()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("CombinationId").AsInt32().Nullable()
            .WithColumn("Name").AsString(200).NotNullable()
            .WithColumn("VariantLabel").AsString(200).Nullable()
            .WithColumn("Sku").AsString(100).Nullable()
            .WithColumn("PictureId").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Quantity").AsInt32().NotNullable()
            .WithColumn("UnitPrice").AsDecimal(18, 4).NotNullable()
            .WithColumn("LineTotal").AsDecimal(18, 4).NotNullable();

        Create.ForeignKey("FK_OrderLine_ShopOrder")
            .FromTable("OrderLine").ForeignColumn("ShopOrderId")
            .ToTable("ShopOrder").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        // Products are soft-deleted and a line must never block that.
        Create.ForeignKey("FK_OrderLine_Product")
            .FromTable("OrderLine").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id");

        Execute.Sql("ALTER TABLE OrderLine ADD CONSTRAINT CK_OrderLine_Quantity CHECK (Quantity BETWEEN 1 AND 10000);");
        Execute.Sql("ALTER TABLE OrderLine ADD CONSTRAINT CK_OrderLine_Amounts CHECK (UnitPrice >= 0 AND LineTotal >= 0);");
        Create.Index("IX_OrderLine_ShopOrder").OnTable("OrderLine").OnColumn("ShopOrderId").Ascending();
        Create.Index("IX_OrderLine_Product").OnTable("OrderLine").OnColumn("ProductId").Ascending();

        Create.Table("ShopOrderHistory")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ShopOrderId").AsInt32().NotNullable()
            .WithColumn("FromStatus").AsInt32().Nullable()
            .WithColumn("ToStatus").AsInt32().NotNullable()
            .WithColumn("ActorType").AsString(20).NotNullable()
            .WithColumn("ActorCustomerId").AsInt32().Nullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_ShopOrderHistory_ShopOrder")
            .FromTable("ShopOrderHistory").ForeignColumn("ShopOrderId")
            .ToTable("ShopOrder").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Execute.Sql("ALTER TABLE ShopOrderHistory ADD CONSTRAINT CK_ShopOrderHistory_Actor CHECK (ActorType IN ('customer', 'shop', 'admin', 'system'));");
        Create.Index("IX_ShopOrderHistory_ShopOrder").OnTable("ShopOrderHistory").OnColumn("ShopOrderId").Ascending().OnColumn("Id").Ascending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'orders.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('orders.manage', 'Manage orders', 'Orders');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'orders.manage'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN PermissionRecord p ON p.Id = m.PermissionRecordId
            WHERE p.SystemName = 'orders.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'orders.manage';
            """);
        Delete.Table("ShopOrderHistory");
        Delete.Table("OrderLine");
        Delete.Table("ShopOrder");
        Delete.Table("CustomerOrder");
    }
}
