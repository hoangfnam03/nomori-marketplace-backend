using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Catalog;

// ---- Public: storefront reads product attributes ----

[ApiController]
[Route("api/v1/products/{productId:int}/attributes")]
[AllowAnonymous]
public sealed class ProductAttributePublicController(IProductAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAttributes(int productId, CancellationToken cancellationToken)
    {
        var detail = await service.GetProductAttributeDetailAsync(productId, cancellationToken);
        return Ok(ToPublicResponse(detail));
    }

    private static object ToPublicResponse(ProductAttributeDetail d) => new
    {
        mappings = d.Mappings.Select(m => new
        {
            m.Mapping.Id,
            m.Mapping.DisplayOrder,
            m.Mapping.IsRequired,
            controlType = m.Mapping.ControlType.ToString(),
            prompt = m.Mapping.TextPrompt ?? m.Attribute.Name,
            attribute = new { m.Attribute.Id, m.Attribute.Name },
            values = m.Values.Select(v => new
            {
                v.Id, v.Name, v.ColorSquaresRgb, v.PriceAdjustment, v.IsPreSelected, v.DisplayOrder
            })
        }),
        combinations = d.Combinations.Select(c => new
        {
            c.Id, c.AttributesJson, c.StockQuantity, c.AllowOutOfStockOrders, c.Sku, c.OverriddenPrice
        })
    };
}

// ---- Admin: global attribute templates ----

[ApiController]
[Route("api/v1/admin/catalog/product-attributes")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminProductAttributeController(IProductAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok(await service.GetAllAttributesAsync(cancellationToken));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSpec(SaveProductAttributeRequest request, CancellationToken cancellationToken)
    {
        var result = await service.CreateAttributeAsync(
            new CreateProductAttributeCommand(request.Name, request.Description, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return CreatedAtAction(nameof(GetAll), result.Value);
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, SaveProductAttributeRequest request, CancellationToken cancellationToken)
    {
        var result = await service.UpdateAttributeAsync(
            new UpdateProductAttributeCommand(id, request.Name, request.Description, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken) =>
        await service.DeleteAttributeAsync(id, cancellationToken) ? NoContent() : NotFound();
}

// ---- Admin: per-product attribute configuration ----

[ApiController]
[Route("api/v1/admin/products/{productId:int}/attributes")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminProductAttributeConfigController(IProductAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetDetail(int productId, CancellationToken cancellationToken) =>
        Ok(await service.GetProductAttributeDetailAsync(productId, cancellationToken));

    // -- Mappings --

    [HttpPost("mappings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMapping(int productId, AddMappingRequest request, CancellationToken cancellationToken)
    {
        var result = await service.AddMappingAsync(
            new CreateAttributeMappingCommand(productId, request.ProductAttributeId, request.TextPrompt,
                request.IsRequired, request.ControlType, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpPut("mappings/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateMapping(int productId, int id, UpdateMappingRequest request, CancellationToken cancellationToken)
    {
        var result = await service.UpdateMappingAsync(
            new UpdateAttributeMappingCommand(id, request.TextPrompt, request.IsRequired, request.ControlType, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpDelete("mappings/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMapping(int productId, int id, CancellationToken cancellationToken) =>
        await service.DeleteMappingAsync(id, cancellationToken) ? NoContent() : NotFound();

    // -- Values --

    [HttpPost("mappings/{mappingId:int}/values")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddValue(int productId, int mappingId, SaveAttributeValueRequest request, CancellationToken cancellationToken)
    {
        var result = await service.AddValueAsync(
            new CreateAttributeValueCommand(mappingId, request.Name, request.ColorSquaresRgb,
                request.PriceAdjustment, request.IsPreSelected, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpPut("mappings/{mappingId:int}/values/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateValue(int productId, int mappingId, int id, SaveAttributeValueRequest request, CancellationToken cancellationToken)
    {
        var result = await service.UpdateValueAsync(
            new UpdateAttributeValueCommand(id, request.Name, request.ColorSquaresRgb,
                request.PriceAdjustment, request.IsPreSelected, request.DisplayOrder),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpDelete("mappings/{mappingId:int}/values/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteValue(int productId, int mappingId, int id, CancellationToken cancellationToken) =>
        await service.DeleteValueAsync(id, cancellationToken) ? NoContent() : NotFound();

    // -- Combinations --

    [HttpGet("combinations")]
    public async Task<IActionResult> GetCombinations(int productId, CancellationToken cancellationToken)
    {
        var detail = await service.GetProductAttributeDetailAsync(productId, cancellationToken);
        return Ok(detail.Combinations);
    }

    [HttpPost("combinations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCombination(int productId, SaveCombinationRequest request, CancellationToken cancellationToken)
    {
        var result = await service.AddCombinationAsync(
            new CreateCombinationCommand(productId, request.AttributesJson, request.StockQuantity,
                request.AllowOutOfStockOrders, request.Sku, request.OverriddenPrice),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpPut("combinations/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateCombination(int productId, int id, SaveCombinationRequest request, CancellationToken cancellationToken)
    {
        var result = await service.UpdateCombinationAsync(
            new UpdateCombinationCommand(id, request.AttributesJson, request.StockQuantity,
                request.AllowOutOfStockOrders, request.Sku, request.OverriddenPrice),
            cancellationToken);
        if (!result.Succeeded)
            return BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
        return Ok(result.Value);
    }

    [HttpDelete("combinations/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCombination(int productId, int id, CancellationToken cancellationToken) =>
        await service.DeleteCombinationAsync(id, cancellationToken) ? NoContent() : NotFound();
}

// ---- DTOs ----

public sealed record SaveProductAttributeRequest(string Name, string? Description = null, int DisplayOrder = 0);

public sealed record AddMappingRequest(
    int ProductAttributeId,
    string? TextPrompt = null,
    bool IsRequired = false,
    AttributeControlType ControlType = AttributeControlType.DropdownList,
    int DisplayOrder = 0);

public sealed record UpdateMappingRequest(
    string? TextPrompt = null,
    bool IsRequired = false,
    AttributeControlType ControlType = AttributeControlType.DropdownList,
    int DisplayOrder = 0);

public sealed record SaveAttributeValueRequest(
    string Name,
    string? ColorSquaresRgb = null,
    decimal PriceAdjustment = 0,
    bool IsPreSelected = false,
    int DisplayOrder = 0);

public sealed record SaveCombinationRequest(
    string AttributesJson = "{}",
    int StockQuantity = 0,
    bool AllowOutOfStockOrders = false,
    string? Sku = null,
    decimal? OverriddenPrice = null);
