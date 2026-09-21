namespace Nomori.Marketplace.Core.Customers;

public interface IEmailVerificationService
{
    Task<string?> SendVerificationAsync(int customerId, CancellationToken cancellationToken);

    Task<bool> VerifyAsync(string token, CancellationToken cancellationToken);
}

public interface IEmailOtpService
{
    Task<EmailOtpResult> CreateAsync(int customerId, string purpose, CancellationToken cancellationToken);

    Task<EmailOtpVerificationResult> VerifyAsync(Guid challengeId, string code, string purpose, CancellationToken cancellationToken);

    Task SetEnabledAsync(int customerId, bool enabled, CancellationToken cancellationToken);
}

public sealed record EmailOtpResult(Guid ChallengeId, DateTime ExpiresOnUtc, string? DevelopmentCode, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}

public sealed record EmailOtpVerificationResult(bool Succeeded, Domain.Customers.Customer? Customer, string? ErrorCode)
{
    public static EmailOtpVerificationResult Failure(string errorCode) => new(false, null, errorCode);

    public static EmailOtpVerificationResult Success(Domain.Customers.Customer customer) => new(true, customer, null);
}
