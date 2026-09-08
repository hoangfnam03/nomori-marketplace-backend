using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Authentication;

[Migration(202609080002)]
public sealed class AuthenticationIdentityMigration : Migration
{
    public override void Up()
    {
        Create.Table("Customer")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CustomerGuid").AsGuid().NotNullable()
            .WithColumn("Email").AsString(320).NotNullable()
            .WithColumn("Username").AsString(100).Nullable()
            .WithColumn("Active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("Deleted").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("FailedLoginAttempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("CannotLoginUntilDateUtc").AsDateTime2().Nullable()
            .WithColumn("RequireReLogin").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("LastLoginDateUtc").AsDateTime2().Nullable();

        Create.Index("IX_Customer_CustomerGuid")
            .OnTable("Customer")
            .OnColumn("CustomerGuid").Ascending()
            .WithOptions().Unique();

        Create.Index("IX_Customer_Email")
            .OnTable("Customer")
            .OnColumn("Email").Ascending()
            .WithOptions().Unique();

        Create.Table("CustomerPassword")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("Password").AsString(512).NotNullable()
            .WithColumn("PasswordFormatId").AsInt32().NotNullable().WithDefaultValue(1)
            .WithColumn("PasswordSalt").AsString(256).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_CustomerPassword_Customer")
            .FromTable("CustomerPassword").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("IX_CustomerPassword_CustomerId_CreatedOnUtc")
            .OnTable("CustomerPassword")
            .OnColumn("CustomerId").Ascending()
            .OnColumn("CreatedOnUtc").Descending();

        Create.Table("CustomerRole")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("Name").AsString(255).NotNullable()
            .WithColumn("SystemName").AsString(255).NotNullable()
            .WithColumn("Active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("IsSystemRole").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Index("IX_CustomerRole_SystemName")
            .OnTable("CustomerRole")
            .OnColumn("SystemName").Ascending()
            .WithOptions().Unique();

        Create.Table("CustomerCustomerRoleMapping")
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("CustomerRoleId").AsInt32().NotNullable();

        Create.PrimaryKey("PK_CustomerCustomerRoleMapping")
            .OnTable("CustomerCustomerRoleMapping")
            .Columns("CustomerId", "CustomerRoleId");

        Create.ForeignKey("FK_CustomerCustomerRoleMapping_Customer")
            .FromTable("CustomerCustomerRoleMapping").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.ForeignKey("FK_CustomerCustomerRoleMapping_CustomerRole")
            .FromTable("CustomerCustomerRoleMapping").ForeignColumn("CustomerRoleId")
            .ToTable("CustomerRole").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);
    }

    public override void Down()
    {
        Delete.Table("CustomerCustomerRoleMapping");
        Delete.Table("CustomerPassword");
        Delete.Table("CustomerRole");
        Delete.Table("Customer");
    }
}