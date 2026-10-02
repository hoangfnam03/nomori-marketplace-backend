using Nomori.Marketplace.Core.Directory;

namespace Nomori.Marketplace.Services.Tests;

/// <summary>A primary currency for services that check price decimals. Two decimals unless a test says otherwise.</summary>
internal sealed class FakePrimaryCurrency(int decimalPlaces = 2) : IPrimaryCurrencyProvider
{
    public Task<Currency> GetPrimaryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", DecimalPlaces = decimalPlaces, IsPrimary = true });
}

/// <summary>In-memory currency table with the same one-primary rule as the database index.</summary>
internal sealed class FakeCurrencyStore : ICurrencyStore
{
    private int nextId = 2;

    public List<Currency> Currencies { get; } =
    [
        new Currency { Id = 1, Code = "USD", Name = "US Dollar", Symbol = "$", DecimalPlaces = 2, RateToPrimary = 1, IsPrimary = true, Published = true }
    ];

    public bool ProductsExist { get; set; }

    public Task<IReadOnlyList<Currency>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Currency>>(Currencies.ToList());

    public Task<Currency?> GetAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(Currencies.FirstOrDefault(c => c.Id == id));

    public Task<Currency?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(Currencies.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase)));

    public Task<Currency?> GetPrimaryAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Currencies.FirstOrDefault(c => c.IsPrimary));

    public Task<int> InsertAsync(Currency currency, CancellationToken cancellationToken)
    {
        currency.Id = nextId++;
        currency.IsPrimary = false;
        Currencies.Add(currency);
        return Task.FromResult(currency.Id);
    }

    public Task UpdateAsync(Currency currency, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        Currencies.RemoveAll(c => c.Id == id && !c.IsPrimary);
        return Task.CompletedTask;
    }

    public Task MakePrimaryAsync(int id, IReadOnlyDictionary<int, decimal> newRates, DateTime nowUtc, CancellationToken cancellationToken)
    {
        foreach (var c in Currencies)
        {
            c.RateToPrimary = newRates[c.Id];
            c.IsPrimary = c.Id == id;
            if (c.IsPrimary) c.Published = true;
        }
        return Task.CompletedTask;
    }

    public Task<bool> AnyProductAsync(CancellationToken cancellationToken) => Task.FromResult(ProductsExist);
}
