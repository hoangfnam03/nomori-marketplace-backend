namespace Nomori.Marketplace.Core.Email;

/// <summary>Settings of the reminder jobs (section <c>Reminders</c>). The jobs themselves are switched on and off in the job screen (F29-A).</summary>
public sealed class ReminderOptions
{
    public const string SectionName = "Reminders";

    /// <summary>An order that still awaits its payment after this long gets one reminder. Keep it below <c>Jobs:AwaitingPaymentMinutes</c>, or the order is cancelled first.</summary>
    public int UnpaidOrderMinutes { get; init; } = 30;

    /// <summary>A cart nobody touched for this long gets a reminder.</summary>
    public int AbandonedCartHours { get; init; } = 24;

    /// <summary>A cart left for longer than this is not reminded about any more: the customer has moved on.</summary>
    public int AbandonedCartMaxDays { get; init; } = 7;

    /// <summary>Reminder records older than this are deleted.</summary>
    public int LogRetentionDays { get; init; } = 90;
}

public static class ReminderKinds
{
    public const string UnpaidOrder = "reminder.unpaid_order";
    public const string AbandonedCart = "reminder.abandoned_cart";
}

/// <summary>An order waiting for its payment, as a reminder needs it.</summary>
public sealed record UnpaidOrderCandidate(int OrderId, string Number, int CustomerId, decimal Total, string CurrencyCode);

/// <summary>A customer whose cart was left alone: how many lines it has and when it was last touched.</summary>
public sealed record AbandonedCartCandidate(int CustomerId, int ItemCount, DateTime LastTouchedUtc);

/// <summary>Who was reminded about what, so nobody is reminded twice. One row per (kind, reference); a cart row is renewed when the cart changes again.</summary>
public interface IReminderStore
{
    /// <summary>Orders awaiting payment (with a pending shop order), made before the cut-off and not reminded yet. Oldest first.</summary>
    Task<IReadOnlyList<UnpaidOrderCandidate>> UnpaidOrdersAsync(DateTime createdBeforeUtc, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Customers whose newest cart line was last touched between the two times and who were not reminded since that touch.
    /// Oldest first.
    /// </summary>
    Task<IReadOnlyList<AbandonedCartCandidate>> AbandonedCartsAsync(DateTime idleBeforeUtc, DateTime idleAfterUtc, int take, CancellationToken cancellationToken);

    /// <summary>
    /// Records the reminder in one statement. False when it was recorded already (and, when <paramref name="renewIfBeforeUtc"/> is given, not
    /// before that time), so two nodes never both send it.
    /// </summary>
    Task<bool> TryRecordAsync(string kind, int referenceId, int customerId, DateTime nowUtc, DateTime? renewIfBeforeUtc, CancellationToken cancellationToken);

    /// <summary>Takes a record back, so the next run tries again (used when the email could not be queued).</summary>
    Task ForgetAsync(string kind, int referenceId, CancellationToken cancellationToken);

    Task<int> DeleteOlderThanAsync(DateTime beforeUtc, int take, CancellationToken cancellationToken);
}
