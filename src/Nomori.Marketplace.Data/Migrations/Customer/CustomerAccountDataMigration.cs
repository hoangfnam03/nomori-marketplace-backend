using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Customer;

[Migration(202609210004)]
public sealed class CustomerAccountDataMigration : Migration
{
    public override void Up()
    {
        Create.Table("CustomerAddress")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity().WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("FirstName").AsString(100).NotNullable().WithColumn("LastName").AsString(100).NotNullable()
            .WithColumn("Company").AsString(100).Nullable().WithColumn("Address1").AsString(200).NotNullable().WithColumn("Address2").AsString(200).Nullable()
            .WithColumn("City").AsString(100).NotNullable().WithColumn("StateProvince").AsString(100).Nullable().WithColumn("CountryCode").AsString(2).NotNullable()
            .WithColumn("ZipPostalCode").AsString(20).Nullable().WithColumn("PhoneNumber").AsString(32).NotNullable().WithColumn("IsDefault").AsBoolean().NotNullable().WithDefaultValue(false);
        Create.ForeignKey("FK_CustomerAddress_Customer").FromTable("CustomerAddress").ForeignColumn("CustomerId").ToTable("Customer").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.Index("IX_CustomerAddress_CustomerId").OnTable("CustomerAddress").OnColumn("CustomerId").Ascending();

        Create.Table("CustomerAttributeDefinition").WithColumn("Id").AsInt32().PrimaryKey().Identity().WithColumn("SystemName").AsString(100).NotNullable().WithColumn("Name").AsString(100).NotNullable().WithColumn("DataType").AsString(20).NotNullable().WithColumn("IsRequired").AsBoolean().NotNullable().WithColumn("DisplayOrder").AsInt32().NotNullable();
        Create.Index("IX_CustomerAttributeDefinition_SystemName").OnTable("CustomerAttributeDefinition").OnColumn("SystemName").Ascending().WithOptions().Unique();
        Create.Table("CustomerAttributeValue").WithColumn("CustomerId").AsInt32().NotNullable().WithColumn("CustomerAttributeDefinitionId").AsInt32().NotNullable().WithColumn("Value").AsString(500).NotNullable();
        Create.PrimaryKey("PK_CustomerAttributeValue").OnTable("CustomerAttributeValue").Columns("CustomerId", "CustomerAttributeDefinitionId");
        Create.ForeignKey("FK_CustomerAttributeValue_Customer").FromTable("CustomerAttributeValue").ForeignColumn("CustomerId").ToTable("Customer").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.ForeignKey("FK_CustomerAttributeValue_Definition").FromTable("CustomerAttributeValue").ForeignColumn("CustomerAttributeDefinitionId").ToTable("CustomerAttributeDefinition").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Execute.Sql("INSERT INTO CustomerAttributeDefinition (SystemName, Name, DataType, IsRequired, DisplayOrder) VALUES ('preferred_language', 'Preferred language', 'text', 0, 1), ('marketing_opt_in', 'Marketing emails', 'boolean', 0, 2);");

        Create.Table("CustomerEmailChangeToken").WithColumn("Id").AsInt32().PrimaryKey().Identity().WithColumn("CustomerId").AsInt32().NotNullable().WithColumn("NewEmail").AsString(320).NotNullable().WithColumn("TokenHash").AsString(128).NotNullable().WithColumn("ExpiresOnUtc").AsDateTime2().NotNullable().WithColumn("Used").AsBoolean().NotNullable().WithDefaultValue(false).WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();
        Create.ForeignKey("FK_CustomerEmailChangeToken_Customer").FromTable("CustomerEmailChangeToken").ForeignColumn("CustomerId").ToTable("Customer").PrimaryColumn("Id").OnDelete(Rule.Cascade);
        Create.Index("IX_CustomerEmailChangeToken_TokenHash").OnTable("CustomerEmailChangeToken").OnColumn("TokenHash").Ascending().WithOptions().Unique();

        Execute.Sql("""INSERT INTO PermissionRecord (SystemName, Name, Category) SELECT code, name, 'Customer' FROM (VALUES ('customer.address.manage','Manage own addresses'),('customer.attributes.manage','Manage own attributes'),('customer.email.change','Change own email')) p(code,name) WHERE NOT EXISTS (SELECT 1 FROM PermissionRecord existing WHERE existing.SystemName=p.code); INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId,CustomerRoleId) SELECT p.Id,r.Id FROM PermissionRecord p CROSS JOIN CustomerRole r WHERE r.SystemName IN ('Registered','Administrator') AND p.SystemName IN ('customer.address.manage','customer.attributes.manage','customer.email.change') AND NOT EXISTS (SELECT 1 FROM PermissionRecordCustomerRoleMapping m WHERE m.PermissionRecordId=p.Id AND m.CustomerRoleId=r.Id);""");
    }
    public override void Down() { Execute.Sql("DELETE m FROM PermissionRecordCustomerRoleMapping m INNER JOIN PermissionRecord p ON p.Id=m.PermissionRecordId WHERE p.SystemName IN ('customer.address.manage','customer.attributes.manage','customer.email.change'); DELETE FROM PermissionRecord WHERE SystemName IN ('customer.address.manage','customer.attributes.manage','customer.email.change');"); Delete.Table("CustomerEmailChangeToken"); Delete.Table("CustomerAttributeValue"); Delete.Table("CustomerAttributeDefinition"); Delete.Table("CustomerAddress"); }
}
