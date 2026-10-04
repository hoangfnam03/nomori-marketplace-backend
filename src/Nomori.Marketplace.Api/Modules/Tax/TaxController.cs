using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Api.Modules.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Tax;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Tax;

/// <summary>Tax categories, the rates of each category by country and state, and the tax category of products. Platform settings.</summary>
[ApiController]
[Route("api/v1/admin/tax")]
[Authorize]
[HasPermission(PermissionCodes.SettingsManage)]
public sealed class AdminTaxController(ITaxService taxService, ICurrentUser currentUser) : ControllerBase
{
    private int ActorId() => int.TryParse(currentUser.Subject, out var customerId) ? customerId : 0;

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategories(CancellationToken cancellationToken) =>
        Ok((await taxService.GetCategoriesAsync(cancellationToken)).Select(TaxCategoryResponse.From));

    [HttpPost("categories")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateCategory(SaveTaxCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await taxService.CreateCategoryAsync(new SaveTaxCategoryCommand(request.Name, request.DisplayOrder), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(TaxCategoryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("categories/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCategory(int id, SaveTaxCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await taxService.UpdateCategoryAsync(id, new SaveTaxCategoryCommand(request.Name, request.DisplayOrder), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(TaxCategoryResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("categories/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCategory(int id, CancellationToken cancellationToken)
    {
        var result = await taxService.DeleteCategoryAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    [HttpGet("categories/{id:int}/rates")]
    public async Task<IActionResult> GetRates(int id, CancellationToken cancellationToken)
    {
        var result = await taxService.GetRatesAsync(id, cancellationToken);
        return result.Succeeded ? Ok(result.Value!.Select(TaxRateResponse.From)) : this.ToFailure(result);
    }

    [HttpPost("categories/{id:int}/rates")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRate(int id, SaveTaxRateRequest request, CancellationToken cancellationToken)
    {
        var result = await taxService.CreateRateAsync(id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(TaxRateResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpPut("rates/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateRate(int id, SaveTaxRateRequest request, CancellationToken cancellationToken)
    {
        var result = await taxService.UpdateRateAsync(id, request.ToCommand(), ActorId(), cancellationToken);
        return result.Succeeded ? Ok(TaxRateResponse.From(result.Value!)) : this.ToFailure(result);
    }

    [HttpDelete("rates/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRate(int id, CancellationToken cancellationToken)
    {
        var result = await taxService.DeleteRateAsync(id, ActorId(), cancellationToken);
        return result.Succeeded ? NoContent() : this.ToFailure(result);
    }

    [HttpGet("products/{productId:int}")]
    public async Task<IActionResult> GetProduct(int productId, CancellationToken cancellationToken)
    {
        var result = await taxService.GetProductAsync(productId, cancellationToken);
        return result.Succeeded ? Ok(ProductTaxResponse.From(result.Value!)) : this.ToFailure(result);
    }

    /// <summary>Assigns the tax category of a product; a null category returns it to the default.</summary>
    [HttpPut("products/{productId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetProduct(int productId, AssignTaxCategoryRequest request, CancellationToken cancellationToken)
    {
        var result = await taxService.SetProductCategoryAsync(productId, request.TaxCategoryId, ActorId(), cancellationToken);
        return result.Succeeded ? Ok(ProductTaxResponse.From(result.Value!)) : this.ToFailure(result);
    }
}

public sealed record SaveTaxCategoryRequest(string? Name, int DisplayOrder = 0);

public sealed record SaveTaxRateRequest(string? CountryCode, int? StateProvinceId, decimal Percentage, bool Published = true)
{
    public SaveTaxRateCommand ToCommand() => new(CountryCode, StateProvinceId, Percentage, Published);
}

public sealed record AssignTaxCategoryRequest(int? TaxCategoryId);

public sealed record TaxCategoryResponse(int Id, string Name, bool IsDefault, int DisplayOrder)
{
    public static TaxCategoryResponse From(TaxCategory c) => new(c.Id, c.Name, c.IsDefault, c.DisplayOrder);
}

public sealed record TaxRateResponse(int Id, int CategoryId, string CountryCode, int? StateProvinceId, decimal Percentage, bool Published)
{
    public static TaxRateResponse From(TaxRate r) => new(r.Id, r.CategoryId, r.CountryCode, r.StateProvinceId, r.Percentage, r.Published);
}

public sealed record ProductTaxResponse(int ProductId, string ProductName, int TaxCategoryId, string TaxCategoryName, bool Assigned)
{
    public static ProductTaxResponse From(ProductTaxView v) => new(v.ProductId, v.ProductName, v.TaxCategoryId, v.TaxCategoryName, v.Assigned);
}
