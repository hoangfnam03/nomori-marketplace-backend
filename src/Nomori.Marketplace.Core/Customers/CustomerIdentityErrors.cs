namespace Nomori.Marketplace.Core.Customers;

public static class CustomerIdentityErrors
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string EmailAlreadyExists = "auth.email_already_exists";
    public const string AccountInactive = "auth.account_inactive";
    public const string AccountLocked = "auth.account_locked";

    public const string PasswordPolicy = "auth.password_policy";

    public const string PasswordRecentlyUsed = "auth.password_recently_used";

    public const string EmailNotVerified = "auth.email_not_verified";

    public const string EmailVerificationInvalid = "auth.email_verification_invalid";

    public const string OtpInvalid = "auth.otp_invalid";

    public const string OtpExpired = "auth.otp_expired";

    public const string OtpLocked = "auth.otp_locked";

    public const string OtpRecentlySent = "auth.otp_recently_sent";
}
