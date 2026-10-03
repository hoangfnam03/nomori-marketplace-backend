using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Customer;

/// <summary>Customer avatar: a media asset with purpose CustomerAvatar. 0 means none, like the other PictureId columns.</summary>
[Migration(202610150001)]
public sealed class CustomerAvatarMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Customer").AddColumn("AvatarPictureId").AsInt32().NotNullable().WithDefaultValue(0);
    }

    public override void Down()
    {
        Delete.Column("AvatarPictureId").FromTable("Customer");
    }
}
