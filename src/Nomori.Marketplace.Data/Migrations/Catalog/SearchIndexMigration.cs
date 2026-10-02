using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Catalog;

/// <summary>F13-A: indexes for the tag and specification filters. No columns change.</summary>
[Migration(202610080001)]
public sealed class SearchIndexMigration : Migration
{
    public override void Up()
    {
        // The primary key of ProductProductTag starts with ProductId, so "products of a tag" needs the reverse order.
        Create.Index("IX_ProductProductTag_TagId")
            .OnTable("ProductProductTag")
            .OnColumn("ProductTagId").Ascending()
            .OnColumn("ProductId").Ascending();

        Create.Index("IX_PSA_Option_Product")
            .OnTable("ProductSpecificationAttribute")
            .OnColumn("SpecificationAttributeOptionId").Ascending()
            .OnColumn("ProductId").Ascending();
    }

    public override void Down()
    {
        Delete.Index("IX_PSA_Option_Product").OnTable("ProductSpecificationAttribute");
        Delete.Index("IX_ProductProductTag_TagId").OnTable("ProductProductTag");
    }
}
