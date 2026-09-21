namespace Nomori.Marketplace.Core.Customers;

public interface ICustomerProfileStore
{
    Task<CustomerProfile?> GetAsync(int customerId, CancellationToken cancellationToken);

    Task UpdateAsync(CustomerProfile profile, CancellationToken cancellationToken);
}

public interface ICustomerProfileService
{
    Task<CustomerProfile?> GetAsync(int customerId, CancellationToken cancellationToken);

    Task<CustomerProfileUpdateResult> UpdateAsync(UpdateCustomerProfileCommand command, CancellationToken cancellationToken);
}

public sealed record CustomerProfileUpdateResult(
    CustomerProfile? Profile,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool Succeeded => Profile is not null && Errors.Count == 0;

    public static CustomerProfileUpdateResult Failure(IReadOnlyDictionary<string, string[]> errors) => new(null, errors);
}
