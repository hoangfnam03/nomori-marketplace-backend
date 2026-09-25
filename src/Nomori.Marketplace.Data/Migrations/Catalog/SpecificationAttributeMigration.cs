using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

[Migration(202609250004)]
public sealed class SpecificationAttributeMigration : Migration
{
    public override void Up()
    {
        Create.Table("SpecificationAttributeGroup")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Table("SpecificationAttribute")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("SpecificationAttributeGroupId").AsInt32().Nullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_SpecAttr_Group")
            .FromTable("SpecificationAttribute").ForeignColumn("SpecificationAttributeGroupId")
            .ToTable("SpecificationAttributeGroup").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.SetNull);

        Create.Index("IX_SpecAttr_GroupId")
            .OnTable("SpecificationAttribute").OnColumn("SpecificationAttributeGroupId");

        Create.Table("SpecificationAttributeOption")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("SpecificationAttributeId").AsInt32().NotNullable()
            .WithColumn("Name").AsString(400).NotNullable()
            .WithColumn("ColorSquaresRgb").AsString(100).Nullable()
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_SAO_SpecAttr")
            .FromTable("SpecificationAttributeOption").ForeignColumn("SpecificationAttributeId")
            .ToTable("SpecificationAttribute").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("IX_SAO_SpecAttrId")
            .OnTable("SpecificationAttributeOption").OnColumn("SpecificationAttributeId");

        Create.Table("ProductSpecificationAttribute")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("AttributeType").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("SpecificationAttributeOptionId").AsInt32().Nullable()
            .WithColumn("CustomValue").AsString(4000).Nullable()
            .WithColumn("AllowFiltering").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("ShowOnProductPage").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

        Create.ForeignKey("FK_PSA_Product")
            .FromTable("ProductSpecificationAttribute").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.ForeignKey("FK_PSA_SAO")
            .FromTable("ProductSpecificationAttribute").ForeignColumn("SpecificationAttributeOptionId")
            .ToTable("SpecificationAttributeOption").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.None);

        Create.Index("IX_PSA_ProductId")
            .OnTable("ProductSpecificationAttribute").OnColumn("ProductId");

        Create.Table("ProductTag")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(400).NotNullable();

        Create.UniqueConstraint("UQ_ProductTag_Name")
            .OnTable("ProductTag").Column("Name");

        Create.Table("ProductProductTag")
            .WithColumn("ProductId").AsInt32().NotNullable()
            .WithColumn("ProductTagId").AsInt32().NotNullable();

        Create.PrimaryKey("PK_ProductProductTag")
            .OnTable("ProductProductTag").Columns("ProductId", "ProductTagId");

        Create.ForeignKey("FK_PPT_Product")
            .FromTable("ProductProductTag").ForeignColumn("ProductId")
            .ToTable("Product").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.ForeignKey("FK_PPT_Tag")
            .FromTable("ProductProductTag").ForeignColumn("ProductTagId")
            .ToTable("ProductTag").PrimaryColumn("Id")
            .OnDelete(System.Data.Rule.Cascade);

        Create.Index("IX_PPT_TagId")
            .OnTable("ProductProductTag").OnColumn("ProductTagId");
    }

    public override void Down()
    {
        Delete.Table("ProductProductTag");
        Delete.Table("ProductTag");
        Delete.Table("ProductSpecificationAttribute");
        Delete.Table("SpecificationAttributeOption");
        Delete.Table("SpecificationAttribute");
        Delete.Table("SpecificationAttributeGroup");
    }
}
