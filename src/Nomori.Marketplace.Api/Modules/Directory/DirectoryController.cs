using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Directory;

/// <summary>Countries and states customers can choose for an address. The server validates every address; these lists only help the form.</summary>
[ApiController]
[Route("api/v1/directory")]
[AllowAnonymous]
public sealed class DirectoryController(IDirectoryService directoryService) : ControllerBase
{
    [HttpGet("countries")]
    public async Task<IActionResult> GetCountries(CancellationToken cancellationToken) =>
        Ok((await directoryService.GetPublishedCountriesAsync(cancellationToken)).Select(PublicCountryResponse.From));

    [HttpGet("countries/{code}/states")]
    public async Task<IActionResult> GetStates(string code, CancellationToken cancellationToken)
    {
        var states = await directoryService.GetPublishedStatesAsync(code, cancellationToken);
        return states is null ? NotFound() : Ok(states.Select(s => new PublicStateResponse(s.Id, s.Code, s.Name)));
    }
}

[ApiController]
[Route("api/v1/admin/directory")]
[Authorize]
[HasPermission(PermissionCodes.SettingsManage)]
public sealed class AdminDirectoryController(IDirectoryService directoryService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet("countries")]
    public async Task<IActionResult> GetCountries(CancellationToken cancellationToken) =>
        Ok((await directoryService.GetCountriesAsync(cancellationToken)).Select(CountryResponse.From));

    [HttpPost("countries")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCountry(SaveCountryRequest request, CancellationToken cancellationToken)
    {
        var result = await directoryService.CreateCountryAsync(request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? CreatedAtAction(nameof(GetCountries), CountryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("countries/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCountry(int id, SaveCountryRequest request, CancellationToken cancellationToken)
    {
        var result = await directoryService.UpdateCountryAsync(id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(CountryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("countries/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCountry(int id, CancellationToken cancellationToken)
    {
        var result = await directoryService.DeleteCountryAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    [HttpGet("countries/{id:int}/states")]
    public async Task<IActionResult> GetStates(int id, CancellationToken cancellationToken)
    {
        var result = await directoryService.GetStatesAsync(id, cancellationToken);
        return result.Succeeded ? Ok(result.Value!.Select(StateResponse.From)) : this.ToFailure(result);
    }

    [HttpPost("countries/{id:int}/states")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateState(int id, SaveStateRequest request, CancellationToken cancellationToken)
    {
        var result = await directoryService.CreateStateAsync(id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? CreatedAtAction(nameof(GetStates), new { id }, StateResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("states/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateState(int id, SaveStateRequest request, CancellationToken cancellationToken)
    {
        var result = await directoryService.UpdateStateAsync(id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(StateResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("states/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteState(int id, CancellationToken cancellationToken)
    {
        var result = await directoryService.DeleteStateAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }
}

public sealed record SaveCountryRequest(
    string? Code = null, string? Alpha3 = null, string? Name = null, bool Published = true, bool AllowsBilling = true, bool AllowsShipping = true,
    bool PostalCodeRequired = false, string? PostalCodePattern = null, int DisplayOrder = 0)
{
    public SaveCountryCommand ToCommand() => new(Code, Alpha3, Name, Published, AllowsBilling, AllowsShipping, PostalCodeRequired, PostalCodePattern, DisplayOrder);
}

public sealed record SaveStateRequest(string? Code = null, string? Name = null, bool Published = true, int DisplayOrder = 0)
{
    public SaveStateCommand ToCommand() => new(Code, Name, Published, DisplayOrder);
}

/// <summary>What a form needs: whether to ask for a state and a postal code. The pattern is a hint; the server decides.</summary>
public sealed record PublicCountryResponse(string Code, string Name, bool HasStates, bool PostalCodeRequired, string? PostalCodePattern, bool AllowsBilling, bool AllowsShipping)
{
    public static PublicCountryResponse From(Country c) =>
        new(c.Code, c.Name, c.PublishedStateCount > 0, c.PostalCodeRequired, c.PostalCodePattern, c.AllowsBilling, c.AllowsShipping);
}

public sealed record PublicStateResponse(int Id, string Code, string Name);

public sealed record CountryResponse(
    int Id, string Code, string? Alpha3, string Name, bool Published, bool AllowsBilling, bool AllowsShipping,
    bool PostalCodeRequired, string? PostalCodePattern, int DisplayOrder, int PublishedStateCount)
{
    public static CountryResponse From(Country c) => new(
        c.Id, c.Code, c.Alpha3, c.Name, c.Published, c.AllowsBilling, c.AllowsShipping, c.PostalCodeRequired, c.PostalCodePattern, c.DisplayOrder, c.PublishedStateCount);
}

public sealed record StateResponse(int Id, int CountryId, string Code, string Name, bool Published, int DisplayOrder)
{
    public static StateResponse From(StateProvince s) => new(s.Id, s.CountryId, s.Code, s.Name, s.Published, s.DisplayOrder);
}
