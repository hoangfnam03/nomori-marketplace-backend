using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Jobs;

/// <summary>F29-A: the schedule and lock of background jobs, their run history and the permission to control them.</summary>
[Migration(202610220001)]
public sealed class JobsMigration : Migration
{
    public override void Up()
    {
        // The rows are created by the runner from the job classes; the table holds only what an administrator may change, plus the lock.
        Create.Table("ScheduledTask")
            .WithColumn("Id").AsInt32().PrimaryKey().Identity()
            .WithColumn("SystemName").AsString(100).NotNullable()
            .WithColumn("Enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("IntervalMinutes").AsInt32().NotNullable()
            // Null means "due now".
            .WithColumn("NextRunUtc").AsDateTime2().Nullable()
            .WithColumn("LockedUntilUtc").AsDateTime2().Nullable()
            .WithColumn("LockOwner").AsString(100).Nullable()
            .WithColumn("LastStartedUtc").AsDateTime2().Nullable()
            .WithColumn("LastFinishedUtc").AsDateTime2().Nullable()
            .WithColumn("LastStatus").AsString(20).Nullable()
            .WithColumn("LastMessage").AsString(500).Nullable()
            .WithColumn("UpdatedOnUtc").AsDateTime2().NotNullable();

        Create.Index("UX_ScheduledTask_SystemName").OnTable("ScheduledTask")
            .OnColumn("SystemName").Ascending().WithOptions().Unique();
        Execute.Sql("ALTER TABLE ScheduledTask ADD CONSTRAINT CK_ScheduledTask_Interval CHECK (IntervalMinutes BETWEEN 1 AND 10080);");

        Create.Table("ScheduledTaskRun")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            .WithColumn("TaskName").AsString(100).NotNullable()
            // schedule or manual
            .WithColumn("TriggerKind").AsString(20).NotNullable()
            .WithColumn("StartedUtc").AsDateTime2().NotNullable()
            .WithColumn("FinishedUtc").AsDateTime2().NotNullable()
            .WithColumn("Status").AsString(20).NotNullable()
            .WithColumn("Processed").AsInt32().NotNullable()
            .WithColumn("Failed").AsInt32().NotNullable()
            .WithColumn("Message").AsString(500).Nullable();

        Create.Index("IX_ScheduledTaskRun_Task_Started").OnTable("ScheduledTaskRun")
            .OnColumn("TaskName").Ascending().OnColumn("StartedUtc").Descending();
        Create.Index("IX_ScheduledTaskRun_Started").OnTable("ScheduledTaskRun").OnColumn("StartedUtc").Ascending();

        Execute.Sql("""
            IF NOT EXISTS (SELECT 1 FROM PermissionRecord WHERE SystemName = 'jobs.manage')
                INSERT INTO PermissionRecord (SystemName, Name, Category)
                VALUES ('jobs.manage', 'Manage background jobs', 'Jobs');

            INSERT INTO PermissionRecordCustomerRoleMapping (PermissionRecordId, CustomerRoleId)
            SELECT p.Id, r.Id
            FROM PermissionRecord p
            CROSS JOIN CustomerRole r
            WHERE r.SystemName = 'Administrator'
              AND p.SystemName = 'jobs.manage'
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
            WHERE p.SystemName = 'jobs.manage';
            DELETE FROM PermissionRecord WHERE SystemName = 'jobs.manage';
            """);
        Delete.Table("ScheduledTaskRun");
        Delete.Table("ScheduledTask");
    }
}
