using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Foundation;

[Migration(202609080001)]
public sealed class FoundationMigration : Migration
{
    public override void Up()
    {
        Create.Table("NomoriSystemMetadata")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Key").AsString(200).NotNullable().Unique()
            .WithColumn("Value").AsString(int.MaxValue).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().Nullable();
    }

    public override void Down()
    {
        Delete.Table("NomoriSystemMetadata");
    }
}