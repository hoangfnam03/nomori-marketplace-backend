using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Authentication;

[Migration(202609080004)]
public sealed class PasswordRecoveryMigration : Migration
{
    public override void Up()
    {
        Create.Table("PasswordRecoveryToken")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("TokenHash").AsString(128).NotNullable()
            .WithColumn("ExpiresOnUtc").AsDateTime2().NotNullable()
            .WithColumn("Used").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_PasswordRecoveryToken_Customer")
            .FromTable("PasswordRecoveryToken").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("IX_PasswordRecoveryToken_TokenHash")
            .OnTable("PasswordRecoveryToken").OnColumn("TokenHash").Ascending().WithOptions().Unique();
    }

    public override void Down() => Delete.Table("PasswordRecoveryToken");
}
