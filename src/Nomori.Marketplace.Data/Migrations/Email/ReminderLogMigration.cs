using FluentMigrator;

namespace Nomori.Marketplace.Data.Migrations.Email;

/// <summary>F22-B: who was reminded about what, so a reminder is sent once.</summary>
[Migration(202610270001)]
public sealed class ReminderLogMigration : Migration
{
    public override void Up()
    {
        Create.Table("ReminderLog")
            .WithColumn("Id").AsInt64().PrimaryKey().Identity()
            // reminder.unpaid_order (ReferenceId is the order) or reminder.abandoned_cart (ReferenceId is the customer)
            .WithColumn("Kind").AsString(50).NotNullable()
            .WithColumn("ReferenceId").AsInt32().NotNullable()
            .WithColumn("CustomerId").AsInt32().NotNullable()
            .WithColumn("SentOnUtc").AsDateTime2().NotNullable();

        // The unique index is what keeps two nodes from sending the same reminder.
        Create.Index("UX_ReminderLog_Kind_Reference").OnTable("ReminderLog")
            .OnColumn("Kind").Ascending().OnColumn("ReferenceId").Ascending().WithOptions().Unique();
        Create.Index("IX_ReminderLog_SentOn").OnTable("ReminderLog").OnColumn("SentOnUtc").Ascending();
    }

    public override void Down() => Delete.Table("ReminderLog");
}
