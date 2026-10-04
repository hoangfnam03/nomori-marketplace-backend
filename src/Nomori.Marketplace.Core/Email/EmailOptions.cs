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

    public string OrderPlacedSubject { get; init; } = "Your Nomori Marketplace order {0} was placed";

    public string StoreOrderNewSubject { get; init; } = "New order {0} for your shop";

    public string StoreOrderCancelledByCustomerSubject { get; init; } = "Order {0} was cancelled by the customer";

    public string StoreOrderConfirmedSubject { get; init; } = "Your order {0} was confirmed";

    public string StoreOrderShippedSubject { get; init; } = "Your order {0} is on its way";

    public string StoreOrderCancelledSubject { get; init; } = "Your order {0} was cancelled";
}
