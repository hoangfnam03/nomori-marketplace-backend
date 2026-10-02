using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Directory;

/// <summary>Published currencies and their rates, for display. Every stored amount is in the primary currency.</summary>
[ApiController]
[Route("api/v1/currencies")]
[AllowAnonymous]
public sealed class CurrencyController(ICurrencyService currencyService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCurrencies(CancellationToken cancellationToken) =>
        Ok((await currencyService.GetPublishedAsync(cancellationToken)).Select(CurrencyResponse.From));
}

[ApiController]
[Route("api/v1/admin/currencies")]
[Authorize]
[HasPermission(PermissionCodes.SettingsManage)]
public sealed class AdminCurrencyController(ICurrencyService currencyService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok((await currencyService.GetAllAsync(cancellationToken)).Select(CurrencyResponse.From));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SaveCurrencyRequest request, CancellationToken cancellationToken)
    {
        var result = await currencyService.CreateAsync(request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded
            ? CreatedAtAction(nameof(GetAll), CurrencyResponse.From(result.Value!))
            : this.ToFailure(result);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, SaveCurrencyRequest request, CancellationToken cancellationToken)
    {
        var result = await currencyService.UpdateAsync(id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(CurrencyResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        var result = await currencyService.DeleteAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    /// <summary>Makes the currency primary and re-bases the other rates. Refused once products exist.</summary>
    [HttpPost("{id:int}/make-primary")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MakePrimary(int id, CancellationToken cancellationToken)
    {
        var result = await currencyService.MakePrimaryAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(CurrencyResponse.From(result.Value!)) : this.ToFailure(result);
    }
}

public sealed record SaveCurrencyRequest(
    string? Code = null, string? Name = null, string? Symbol = null, int DecimalPlaces = 2, decimal Rate = 1, bool Published = true, int DisplayOrder = 0)
{
    public SaveCurrencyCommand ToCommand() => new(Code, Name, Symbol, DecimalPlaces, Rate, Published, DisplayOrder);
}

public sealed record CurrencyResponse(
    int Id, string Code, string Name, string? Symbol, int DecimalPlaces, decimal Rate, bool IsPrimary, bool Published, int DisplayOrder, DateTime RateUpdatedOnUtc)
{
    public static CurrencyResponse From(Currency c) =>
        new(c.Id, c.Code, c.Name, c.Symbol, c.DecimalPlaces, c.RateToPrimary, c.IsPrimary, c.Published, c.DisplayOrder, c.RateUpdatedOnUtc);
}
