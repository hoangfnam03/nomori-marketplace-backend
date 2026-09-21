namespace Nomori.Marketplace.Core.Customers;

public sealed class CustomerAddress
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string? StateProvince { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string? ZipPostalCode { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

public sealed record CustomerAttributeDefinition(string SystemName, string Name, string DataType, bool IsRequired, int DisplayOrder);
public sealed record CustomerAttributeValue(string SystemName, string Value);
public sealed record CustomerAttributeSet(IReadOnlyList<CustomerAttributeDefinition> Definitions, IReadOnlyDictionary<string, string> Values);

public sealed record EmailChangeRequestResult(bool Succeeded, string? ErrorCode, string? DevelopmentToken = null);

public interface ICustomerAccountDataStore
{
    Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken);
    Task<CustomerAddress?> GetAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    Task<int> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken);
    Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken);
    Task SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken);
    Task CreateEmailChangeAsync(int customerId, string newEmail, string tokenHash, DateTime expiresOnUtc, CancellationToken cancellationToken);
    Task<(int CustomerId, string NewEmail)?> ConsumeEmailChangeAsync(string tokenHash, DateTime nowUtc, CancellationToken cancellationToken);
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);
    Task ApplyEmailChangeAsync(int customerId, string newEmail, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface ICustomerAccountDataService
{
    Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken);
    Task<(CustomerAddress? Address, IReadOnlyDictionary<string, string[]> Errors)> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken);
    Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken);
    Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, string[]>> SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken);
    Task<EmailChangeRequestResult> RequestEmailChangeAsync(int customerId, string newEmail, string currentPassword, CancellationToken cancellationToken);
    Task<bool> ConfirmEmailChangeAsync(string token, CancellationToken cancellationToken);
}
