using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Catalog;

// ---- Public: product spec attributes + tags ----

[ApiController]
[Route("api/v1/products/{productId:int}/specs")]
[AllowAnonymous]
public sealed class ProductSpecPublicController(ISpecificationAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSpecs(int productId, CancellationToken cancellationToken) =>
        Ok(await service.GetProductSpecDetailAsync(productId, cancellationToken));
}

[ApiController]
[Route("api/v1/products/{productId:int}/tags")]
[AllowAnonymous]
public sealed class ProductTagPublicController(ISpecificationAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetTags(int productId, CancellationToken cancellationToken) =>
        Ok(await service.GetProductTagsAsync(productId, cancellationToken));
}

// ---- Admin: global spec attribute definitions ----

[ApiController]
[Route("api/v1/admin/catalog/spec-attributes")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminSpecAttributeController(ISpecificationAttributeService service) : ControllerBase
{
    [HttpGet("groups")]
    public async Task<IActionResult> GetGroups(CancellationToken ct) => Ok(await service.GetGroupsAsync(ct));

    [HttpPost("groups")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateGroup(SaveSpecGroupRequest req, CancellationToken ct)
    {
        var r = await service.CreateGroupAsync(new CreateSpecGroupCommand(req.Name, req.DisplayOrder), ct);
        return r.Succeeded ? Ok(r.Value) : BadRequest(new ValidationProblemDetails(r.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpPut("groups/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateGroup(int id, SaveSpecGroupRequest req, CancellationToken ct)
    {
        var r = await service.UpdateGroupAsync(new UpdateSpecGroupCommand(id, req.Name, req.DisplayOrder), ct);
        return r.Succeeded ? Ok(r.Value) : BadRequest(new ValidationProblemDetails(r.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpDelete("groups/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteGroup(int id, CancellationToken ct) =>
        await service.DeleteGroupAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await service.GetAllAsync(ct));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(SaveSpecAttributeRequest req, CancellationToken ct)
    {
        var r = await service.CreateAsync(new CreateSpecAttributeCommand(req.Name, req.GroupId, req.DisplayOrder), ct);
        return r.Succeeded ? Ok(r.Value) : BadRequest(new ValidationProblemDetails(r.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(int id, SaveSpecAttributeRequest req, CancellationToken ct)
    {
        var r = await service.UpdateAsync(new UpdateSpecAttributeCommand(id, req.Name, req.GroupId, req.DisplayOrder), ct);
        return r.Succeeded ? Ok(r.Value) : BadRequest(new ValidationProblemDetails(r.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await service.DeleteAsync(id, ct) ? NoContent() : NotFound();

    [HttpGet("{id:int}/options")]
    public async Task<IActionResult> GetOptions(int id, CancellationToken ct) => Ok(await service.GetOptionsAsync(id, ct));

    [HttpPost("{id:int}/options")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateOption(int id, SaveSpecOptionRequest req, CancellationToken ct)
    {
        var r = await service.CreateOptionAsync(new CreateSpecOptionCommand(id, req.Name, req.ColorSquaresRgb, req.DisplayOrder), ct);
        return r.Succeeded ? Ok(r.Value) : BadRequest(new ValidationProblemDetails(r.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpPut("{id:int}/options/{optionId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOption(int id, int optionId, SaveSpecOptionRequest req, CancellationToken ct)
    {
        var r = await service.UpdateOptionAsync(new UpdateSpecOptionCommand(optionId, req.Name, req.ColorSquaresRgb, req.DisplayOrder), ct);
        return r.Succeeded ? Ok(r.Value) : BadRequest(new ValidationProblemDetails(r.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpDelete("{id:int}/options/{optionId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteOption(int id, int optionId, CancellationToken ct) =>
        await service.DeleteOptionAsync(optionId, ct) ? NoContent() : NotFound();
}

// ---- Admin: per-product spec mappings ----

[ApiController]
[Route("api/v1/admin/products/{productId:int}/specs")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminProductSpecController(ISpecificationAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSpecs(int productId, CancellationToken ct) =>
        Ok(await service.GetProductSpecDetailAsync(productId, ct));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSpec(int productId, AddProductSpecRequest req, CancellationToken ct)
    {
        var result = await service.AddProductSpecAsync(
            new AddProductSpecCommand(productId, req.AttributeType, req.SpecificationAttributeOptionId,
                req.CustomValue, req.AllowFiltering, req.ShowOnProductPage, req.DisplayOrder), ct);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpPut("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSpec(int productId, int id, AddProductSpecRequest req, CancellationToken ct)
    {
        var result = await service.UpdateProductSpecAsync(
            new UpdateProductSpecCommand(id, req.AttributeType, req.SpecificationAttributeOptionId,
                req.CustomValue, req.AllowFiltering, req.ShowOnProductPage, req.DisplayOrder), ct);
        return result.Succeeded ? Ok(result.Value) : BadRequest(new ValidationProblemDetails(result.Errors.ToDictionary(e => e.Key, e => e.Value)));
    }

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSpec(int productId, int id, CancellationToken ct) =>
        await service.DeleteProductSpecAsync(id, ct) ? NoContent() : NotFound();
}

// ---- Admin: tags ----

[ApiController]
[Route("api/v1/admin/catalog/tags")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminProductTagController(ISpecificationAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct) => Ok(await service.GetAllTagsAsync(ct));

    [HttpDelete("{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await service.DeleteTagAsync(id, ct) ? NoContent() : NotFound();
}

[ApiController]
[Route("api/v1/admin/products/{productId:int}/tags")]
[Authorize]
[HasPermission(PermissionCodes.CatalogManage)]
public sealed class AdminProductTagMappingController(ISpecificationAttributeService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetTags(int productId, CancellationToken ct) =>
        Ok(await service.GetProductTagsAsync(productId, ct));

    [HttpPut]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetTags(int productId, SetProductTagsRequest req, CancellationToken ct)
    {
        await service.SetProductTagsAsync(productId, req.TagNames, ct);
        return Ok(await service.GetProductTagsAsync(productId, ct));
    }
}

// ---- DTOs ----

public sealed record SaveSpecGroupRequest(string Name, int DisplayOrder = 0);
public sealed record SaveSpecAttributeRequest(string Name, int? GroupId = null, int DisplayOrder = 0);
public sealed record SaveSpecOptionRequest(string Name, string? ColorSquaresRgb = null, int DisplayOrder = 0);

public sealed record AddProductSpecRequest(
    SpecificationAttributeType AttributeType = SpecificationAttributeType.Option,
    int? SpecificationAttributeOptionId = null,
    string? CustomValue = null,
    bool AllowFiltering = false,
    bool ShowOnProductPage = true,
    int DisplayOrder = 0);

public sealed record SetProductTagsRequest(string[] TagNames);
