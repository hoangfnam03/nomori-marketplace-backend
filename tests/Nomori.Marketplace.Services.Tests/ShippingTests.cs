using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>Stubs shared by the shipping tests. Shipping is GHN only: the quote itself is tested in <see cref="GhnTests"/>.</summary>
public static class ShippingTests
{
    internal sealed class StubCart : ICartService
    {
        public CartView View { get; set; } = new("USD", [], 0, 0, false);

        public Task<CartView> GetAsync(int customerId, CancellationToken cancellationToken) => Task.FromResult(View);
        public Task<int> CountAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> AddAsync(int customerId, AddToCartCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> SetQuantityAsync(int customerId, int lineId, int quantity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CatalogResult<CartView>> RemoveAsync(int customerId, int lineId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> ClearAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CartView> AcceptPricesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class StubAddresses : ICustomerAccountDataService
    {
        public List<CustomerAddress> Addresses { get; } = [];

        public Task<IReadOnlyList<CustomerAddress>> GetAddressesAsync(int customerId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CustomerAddress>>(Addresses.Where(a => a.CustomerId == customerId).ToList());

        public Task<(CustomerAddress? Address, IReadOnlyDictionary<string, string[]> Errors)> SaveAddressAsync(CustomerAddress address, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAddressAsync(int customerId, int addressId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerAttributeSet> GetAttributesAsync(int customerId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string[]>> SaveAttributesAsync(int customerId, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EmailChangeRequestResult> RequestEmailChangeAsync(int customerId, string newEmail, string currentPassword, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ConfirmEmailChangeAsync(string token, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
