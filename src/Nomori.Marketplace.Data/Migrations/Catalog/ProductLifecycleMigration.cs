using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>
/// F10-B: four-state product lifecycle. <c>Status</c> becomes the single source of truth and <c>Published</c> turns into a
/// persisted computed column (<c>Status = 1</c>), so every existing query that filters on it keeps working and cannot drift.
/// </summary>
[Migration(202610040001)]
public sealed class ProductLifecycleMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Product")
            .AddColumn("Status").AsInt32().NotNullable().WithDefaultValue(0)
            .AddColumn("StatusBeforeHidden").AsInt32().Nullable()
            .AddColumn("HiddenReason").AsString(2000).Nullable()
            .AddColumn("HiddenOnUtc").AsDateTime2().Nullable()
            .AddColumn("HiddenByCustomerId").AsInt32().Nullable()
            .AddColumn("ReviewRequestedOnUtc").AsDateTime2().Nullable();

        Create.ForeignKey("FK_Product_HiddenByCustomer")
            .FromTable("Product").ForeignColumn("HiddenByCustomerId")
            .ToTable("Customer").PrimaryColumn("Id");

        // Published products are live; everything else was never distinguishable from a draft.
        Execute.Sql("UPDATE Product SET Status = CASE WHEN Published = 1 THEN 1 ELSE 0 END;");

        Execute.Sql("DROP INDEX IX_Product_Published_Deleted ON Product;");
        DropPublishedDefaultConstraint();
        Delete.Column("Published").FromTable("Product");

        Execute.Sql("ALTER TABLE Product ADD Published AS (CASE WHEN Status = 1 THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END) PERSISTED;");
        Execute.Sql("CREATE INDEX IX_Product_Published_Deleted ON Product (Published, Deleted);");
        Execute.Sql("CREATE INDEX IX_Product_Status_VendorId ON Product (Status, VendorId);");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IX_Product_Status_VendorId ON Product;");
        Execute.Sql("DROP INDEX IX_Product_Published_Deleted ON Product;");
        Execute.Sql("ALTER TABLE Product DROP COLUMN Published;");
        Execute.Sql("ALTER TABLE Product ADD Published bit NOT NULL CONSTRAINT DF_Product_Published DEFAULT 1;");
        Execute.Sql("UPDATE Product SET Published = CASE WHEN Status = 1 THEN 1 ELSE 0 END;");
        Execute.Sql("CREATE INDEX IX_Product_Published_Deleted ON Product (Published, Deleted);");

        Delete.ForeignKey("FK_Product_HiddenByCustomer").OnTable("Product");
        Delete.Column("ReviewRequestedOnUtc").FromTable("Product");
        Delete.Column("HiddenByCustomerId").FromTable("Product");
        Delete.Column("HiddenOnUtc").FromTable("Product");
        Delete.Column("HiddenReason").FromTable("Product");
        Delete.Column("StatusBeforeHidden").FromTable("Product");
        Delete.Column("Status").FromTable("Product");
    }

    /// <summary>
    /// The original <c>Published</c> column has a default constraint whose name SQL Server generated, so it has to be found before the column can be dropped.
    /// </summary>
    private void DropPublishedDefaultConstraint() =>
        Execute.Sql("""
            DECLARE @name sysname = (
                SELECT dc.name FROM sys.default_constraints dc
                INNER JOIN sys.columns c ON c.default_object_id = dc.object_id
                WHERE dc.parent_object_id = OBJECT_ID('Product') AND c.name = 'Published');
            IF @name IS NOT NULL EXEC('ALTER TABLE Product DROP CONSTRAINT [' + @name + ']');
            """);
}
