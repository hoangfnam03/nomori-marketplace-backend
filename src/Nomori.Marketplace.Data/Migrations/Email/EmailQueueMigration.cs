using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Email;

/// <summary>F22-A: the email queue and the permission to manage it.</summary>
[Migration(202610250001)]
public sealed class EmailQueueMigration : Migration
{
    public override void Up()
    {
        Create.Table("QueuedEmail")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("Kind").AsString(50).NotNullable()
            .WithColumn("ToAddress").AsString(320).NotNullable()
            .WithColumn("Subject").AsString(255).NotNullable()
            .WithColumn("HtmlBody").AsString(int.MaxValue).NotNullable()
            .WithColumn("TextBody").AsString(int.MaxValue).Nullable()
            // pending, sending, sent or failed
            .WithColumn("Status").AsString(20).NotNullable()
            .WithColumn("Attempts").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("NextAttemptUtc").AsDateTime2().NotNullable()
            .WithColumn("LockedUntilUtc").AsDateTime2().Nullable()
            .WithColumn("LastError").AsString(500).Nullable()
            .WithColumn("CreatedOnUtc").AsDateTime2().NotNullable()
            .WithColumn("SentOnUtc").AsDateTime2().Nullable();

        Execute.Sql("ALTER TABLE QueuedEmail ADD CONSTRAINT CK_QueuedEmail_Status CHECK (Status IN ('pending', 'sending', 'sent', 'failed'));");

        // What the sender asks for: the emails that are due.
        Create.Index("IX_QueuedEmail_Status_NextAttempt").OnTable("QueuedEmail")
            .OnColumn("Status").Ascending().OnColumn("NextAttemptUtc").Ascending();
        // The admin list (newest first) and the clean-up.
        Create.Index("IX_QueuedEmail_Created").OnTable("QueuedEmail").OnColumn("CreatedOnUtc").Descending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'emails.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('emails.manage', 'Manage the email queue', 'Email');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'emails.manage'
              AND NOT EXISTS (
                  SELECT 1 FROM PermissionRecordCustomerRoleMapping m
                  WHERE m.PermissionRecordId = p.Id AND m.CustomerRoleId = r.Id
              );
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DELETE m FROM PermissionRecordCustomerRoleMapping m
            INNER JOIN PermissionRecord p ON p.Id = m.PermissionRecordId
            WHERE p.SystemName = 'emails.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'emails.manage';
            """);
        Delete.Table("QueuedEmail");
    }
}
