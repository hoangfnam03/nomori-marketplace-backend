using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>F12-A: stock ledger, reservations and the per-product tracking settings.</summary>
[Migration(202610070001)]
public sealed class InventoryMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Product")
            .AddColumn("TrackInventory").AsBoolean().NotNullable().WithDefaultValue(true)
            .AddColumn("LowStockThreshold").AsInt32().NotNullable().WithDefaultValue(5);

        Create.Table("StockMovement")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            // No foreign key: combinations are replaced when variants are saved, the history must survive that.
            .WithColumn("CombinationId").AsInt32().Nullable()
            .WithColumn("Delta").AsInt32().NotNullable()
            .WithColumn("QuantityAfter").AsInt32().NotNullable()
            .WithColumn("Reason").AsString(50).NotNullable()
            .WithColumn("Reference").AsString(100).Nullable()
            .WithColumn("Note").AsString(500).Nullable()
            .WithColumn("ActorCustomerId").AsInt32().Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_StockMovement_Product")
            .FromTable("StockMovement").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.None);

        Create.Index("IX_StockMovement_ProductId")
            .OnTable("StockMovement")
            .OnColumn("ProductId").Ascending()
            .OnColumn("Id").Descending();

        Create.Table("StockReservation")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("CombinationId").AsInt32().Nullable()
            .WithColumn("Quantity").AsInt32().NotNullable()
            .WithColumn("Reference").AsString(100).NotNullable()
            // 0 active, 1 committed, 2 released
            .WithColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("ExpiresOnUtc").AsDateTime2().NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_StockReservation_Product")
            .FromTable("StockReservation").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.None);

        Create.Index("IX_StockReservation_Product_Status")
            .OnTable("StockReservation")
            .OnColumn("ProductId").Ascending()
            .OnColumn("Status").Ascending()
            .OnColumn("ExpiresOnUtc").Ascending();

        // One active hold per reference and item makes reserving again a replace instead of a double hold.
        Execute.Sql("CREATE UNIQUE INDEX UX_StockReservation_Active ON StockReservation (Reference, ProductId, CombinationId) WHERE Status = 0;");
        Execute.Sql("CREATE INDEX IX_StockReservation_Reference ON StockReservation (Reference, Status);");
    }

    public override void Down()
    {
        Delete.Table("StockReservation");
        Delete.Table("StockMovement");
        Delete.Column("LowStockThreshold").FromTable("Product");
        Delete.Column("TrackInventory").FromTable("Product");
    }
}
