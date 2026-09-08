namespace Nomori.Marketplace.Core.Customers;

public static class CustomerIdentityErrors
{
    public const string InvalidCredentials = "auth.invalid_credentials";
    public const string EmailAlreadyExists = "auth.email_already_exists";
    public const string AccountInactive = "auth.account_inactive";
    public const string AccountLocked = "auth.account_locked";
}