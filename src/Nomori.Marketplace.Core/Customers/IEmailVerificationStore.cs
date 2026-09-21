namespace Nomori.Marketplace.Core.Customers;

public interface IEmailVerificationStore
{
    Task CreateAsync(int customerId, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken);

    Task<(int CustomerId, DateTime ExpiresOnUtc, bool Used)?> FindAsync(string tokenHash, CancellationToken cancellationToken);

    Task<int?> ConsumeAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken);

    Task MarkVerifiedAsync(int customerId, DateTime verifiedOnUtc, CancellationToken cancellationToken);
}
