using System.Data;
using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Authentication;

/// <summary>
/// Adds Nomori email verification, email OTP and security audit primitives.
/// Tokens and OTP values are stored only as hashes.
/// </summary>
[Migration(202609210002)]
public sealed class EmailVerificationOtpAuditMigration : Migration
{
    public override void Up()
    {
        Alter.Table("Customer")
            .AddColumn("EmailVerified").AsBoolean().NotNullable().WithDefaultValue(false)
            .AddColumn("EmailVerifiedOnUtc").AsDateTime2().Nullable()
            .AddColumn("EmailOtpEnabled").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.Table("EmailVerificationToken")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("TokenHash").AsString(128).NotNullable()
            .WithColumn("ExpiresOnUtc").AsDateTime2().NotNullable()
            .WithColumn("Used").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_EmailVerificationToken_Customer")
            .FromTable("EmailVerificationToken").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("IX_EmailVerificationToken_TokenHash")
            .OnTable("EmailVerificationToken").OnColumn("TokenHash").Ascending().WithOptions().Unique();

        Create.Index("IX_EmailVerificationToken_CustomerId")
            .OnTable("EmailVerificationToken").OnColumn("CustomerId").Ascending();

        Create.Table("EmailOtpChallenge")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("ChallengeId").AsGuid().NotNullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("Purpose").AsString(50).NotNullable()
            .WithColumn("CodeHash").AsString(128).NotNullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("ExpiresOnUtc").AsDateTime2().NotNullable()
            .WithColumn("LastSentOnUtc").AsDateTime2().Nullable()
            .WithColumn("FailedAttempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("Used").AsBoolean().NotNullable().WithDefaultValue(false);

        Create.ForeignKey("FK_EmailOtpChallenge_Customer")
            .FromTable("EmailOtpChallenge").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.Cascade);

        Create.Index("IX_EmailOtpChallenge_ChallengeId")
            .OnTable("EmailOtpChallenge").OnColumn("ChallengeId").Ascending().WithOptions().Unique();

        Create.Index("IX_EmailOtpChallenge_CustomerId_Purpose_CreatedOnUtc")
            .OnTable("EmailOtpChallenge").OnColumn("CustomerId").Ascending()
            .OnColumn("Purpose").Ascending().OnColumn("CreatedOnUtc").Descending();

        Create.Table("AuditLog")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("EventName").AsString(150).NotNullable()
            .WithColumn("CustomerId").AsInt32().Nullable()
            .WithColumn("TargetCustomerId").AsInt32().Nullable()
            .WithColumn("EntityType").AsString(100).Nullable()
            .WithColumn("EntityId").AsInt32().Nullable()
            .WithColumn("IpAddress").AsString(64).Nullable()
            .WithColumn("DetailsJson").AsString(int.MaxValue).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable();

        Create.ForeignKey("FK_AuditLog_Customer")
            .FromTable("AuditLog").ForeignColumn("CustomerId")
            .ToTable("Customer").PrimaryColumn("Id")
            .OnDelete(Rule.SetNull);

        Create.Index("IX_AuditLog_CreatedOnUtc")
            .OnTable("AuditLog").OnColumn("CreatedOnUtc").Descending();

        Create.Index("IX_AuditLog_CustomerId_CreatedOnUtc")
            .OnTable("AuditLog").OnColumn("CustomerId").Ascending()
            .OnColumn("CreatedOnUtc").Descending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'admin.audit.read')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('admin.audit.read', 'Read security audit logs', 'Administration');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'admin.audit.read'
              AND NOT EXISTS
              (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping existingMapping
                  WHERE existingMapping.PermissionRecordId = p.Id
                    AND existingMapping.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE mapping
            FROM PermissionRecordCustomerRoleMapping mapping
            INNER JOIN PermissionRecord permission ON permission.Id = mapping.PermissionRecordId
            WHERE permission.SystemName = 'admin.audit.read';
            DELETE FROM PermissionRecord WHERE SystemName = 'admin.audit.read';
            """);

        Delete.Table("AuditLog");
        Delete.Table("EmailOtpChallenge");
        Delete.Table("EmailVerificationToken");
        Delete.Column("EmailOtpEnabled").FromTable("Customer");
        Delete.Column("EmailVerifiedOnUtc").FromTable("Customer");
        Delete.Column("EmailVerified").FromTable("Customer");
    }
}
