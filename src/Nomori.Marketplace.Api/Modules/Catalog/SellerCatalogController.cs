using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Catalog;

/// <summary>Taxonomy as sellers see it. Sellers only read this; they never create, change or delete categories.</summary>
[ApiController]
[Route("api/v1/catalog/categories/selectable")]
[Authorize]
[HasPermission(PermissionCodes.VendorPortal)]
public sealed class SellerCatalogController(ICategoryService categoryService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSelectableCategories(CancellationToken cancellationToken)
    {
        var categories = await categoryService.GetSelectableForSellersAsync(cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(categories.Select(c => new SelectableCategoryResponse(c.Id, c.Name, c.ParentCategoryId, c.Path)));
    }
}

public sealed record SelectableCategoryResponse(int Id, string Name, int ParentCategoryId, string Path);
