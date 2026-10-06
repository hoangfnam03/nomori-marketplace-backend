namespace Nomori.Marketplace.Core.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; init; }

    public string SmtpHost { get; init; } = string.Empty;

    public int SmtpPort { get; init; } = 587;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string FromAddress { get; init; } = string.Empty;

    public string FromName { get; init; } = "Nomori Marketplace";

    public bool UseSsl { get; init; } = true;

    public bool UseStartTls { get; init; } = true;

    public string FrontendBaseUrl { get; init; } = string.Empty;

    public string PasswordRecoverySubject { get; init; } = "Reset your Nomori Marketplace password";

    public string EmailVerificationSubject { get; init; } = "Verify your Nomori Marketplace email";

    public string EmailOtpSubject { get; init; } = "Your Nomori Marketplace verification code";

    public string VendorApplicationApprovedSubject { get; init; } = "Your Nomori Marketplace shop application was approved";

    public string VendorApplicationRejectedSubject { get; init; } = "Your Nomori Marketplace shop application was not approved";

    public string ProductHiddenSubject { get; init; } = "A product of your shop was hidden";

    public string ProductUnhiddenSubject { get; init; } = "A product of your shop is visible again";

    public string VendorMemberSetupSubject { get; init; } = "You have been added to a shop on Nomori Marketplace";

    // ---- Queue (F22-A) ----

    /// <summary>A queued email that failed this many times is not tried again until an administrator retries it.</summary>
    public int QueueMaxAttempts { get; init; } = 5;

    /// <summary>How long a started send keeps its email. A node that dies mid-send frees it after this.</summary>
    public int QueueLeaseMinutes { get; init; } = 10;

    public int SentRetentionDays { get; init; } = 30;

    public int FailedRetentionDays { get; init; } = 90;

    /// <summary>Order emails. <c>{number}</c> is replaced by the order number.</summary>
    public string OrderPlacedSubject { get; init; } = "Your Nomori Marketplace order {number}";

    public string OrderShippedSubject { get; init; } = "Your order {number} was shipped";

    public string OrderDeliveredSubject { get; init; } = "Your order {number} was delivered";

    public string OrderCancelledSubject { get; init; } = "Your order {number} was cancelled";
}
