using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Services.Authentication;

public sealed record PasswordPolicyRequirements(
    int MinimumLength,
    bool RequiresUppercase,
    bool RequiresLowercase,
    bool RequiresDigit,
    bool RequiresNonAlphanumeric);

public interface IPasswordPolicy
{
    PasswordPolicyRequirements Requirements { get; }

    IReadOnlyList<string> Validate(string password);
}

public sealed class PasswordPolicy(IOptions<SecurityOptions> securityOptions) : IPasswordPolicy
{
    public PasswordPolicyRequirements Requirements => new(
        securityOptions.Value.MinimumPasswordLength,
        RequiresUppercase: true,
        RequiresLowercase: true,
        RequiresDigit: true,
        RequiresNonAlphanumeric: true);

    public IReadOnlyList<string> Validate(string password)
    {
        var requirements = Requirements;
        var errors = new List<string>();

        if (string.IsNullOrEmpty(password) || password.Length < requirements.MinimumLength)
            errors.Add($"Password must be at least {requirements.MinimumLength} characters.");
        if (requirements.RequiresUppercase && !password.Any(char.IsUpper))
            errors.Add("Password must contain an uppercase letter.");
        if (requirements.RequiresLowercase && !password.Any(char.IsLower))
            errors.Add("Password must contain a lowercase letter.");
        if (requirements.RequiresDigit && !password.Any(char.IsDigit))
            errors.Add("Password must contain a number.");
        if (requirements.RequiresNonAlphanumeric && password.All(char.IsLetterOrDigit))
            errors.Add("Password must contain a special character.");

        return errors;
    }
}
