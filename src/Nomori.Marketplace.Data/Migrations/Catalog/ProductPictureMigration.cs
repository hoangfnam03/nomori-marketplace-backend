using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>F11-A: ordered pictures of a product. The primary key is the media asset, so a picture belongs to at most one product.</summary>
[Migration(202610050001)]
public sealed class ProductPictureMigration : Migration
{
    public override void Up()
    {
        Create.Table("ProductPicture")
            .WithColumn("MediaAssetId").AsInt32().PrimaryKey()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_ProductPicture_MediaAsset")
            .FromTable("ProductPicture").ForeignColumn("MediaAssetId")
            .ToTable("MediaAsset").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("FK_ProductPicture_Product")
            .FromTable("ProductPicture").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("IX_ProductPicture_ProductId")
            .OnTable("ProductPicture")
            .OnColumn("ProductId").Ascending()
            .OnColumn("DisplayOrder").Ascending();
    }

    public override void Down() => Delete.Table("ProductPicture");
}
