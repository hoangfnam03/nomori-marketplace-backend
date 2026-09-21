using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Web.Framework.Security;

namespace Nomori.Marketplace.Api.Modules.Customer;

[ApiController]
[Route("api/v1/customer")]
[Authorize]
public sealed class CustomerAccountDataController(ICurrentUser currentUser, ICustomerAccountDataService service) : ControllerBase
{
    [HttpGet("addresses")][HasPermission(PermissionCodes.CustomerAddressManage)] public async Task<IActionResult> Addresses(CancellationToken ct) => Id(out var id) ? Ok(await service.GetAddressesAsync(id,ct)) : Unauthorized();
    [HttpPost("addresses")][HasPermission(PermissionCodes.CustomerAddressManage)][ValidateAntiForgeryToken] public Task<IActionResult> Create(AddressRequest request,CancellationToken ct)=>Save(0,request,ct);
    [HttpPut("addresses/{addressId:int}")][HasPermission(PermissionCodes.CustomerAddressManage)][ValidateAntiForgeryToken] public Task<IActionResult> Update(int addressId,AddressRequest request,CancellationToken ct)=>Save(addressId,request,ct);
    [HttpDelete("addresses/{addressId:int}")][HasPermission(PermissionCodes.CustomerAddressManage)][ValidateAntiForgeryToken] public async Task<IActionResult> Delete(int addressId,CancellationToken ct){if(!Id(out var id))return Unauthorized();return await service.DeleteAddressAsync(id,addressId,ct)?NoContent():NotFound();}
    [HttpGet("attributes")][HasPermission(PermissionCodes.CustomerAttributesManage)] public async Task<IActionResult> Attributes(CancellationToken ct)=>Id(out var id)?Ok(await service.GetAttributesAsync(id,ct)):Unauthorized();
    [HttpPut("attributes")][HasPermission(PermissionCodes.CustomerAttributesManage)][ValidateAntiForgeryToken] public async Task<IActionResult> UpdateAttributes(Dictionary<string,string?> values,CancellationToken ct){if(!Id(out var id))return Unauthorized();var errors=await service.SaveAttributesAsync(id,values,ct);return errors.Count==0?NoContent():BadRequest(new ValidationProblemDetails(errors.ToDictionary(x=>x.Key,x=>x.Value)));}
    [HttpPost("email-change/request")][HasPermission(PermissionCodes.CustomerEmailChange)][ValidateAntiForgeryToken] public async Task<IActionResult> RequestEmailChange(EmailChangeRequest request,CancellationToken ct){if(!Id(out var id))return Unauthorized();var result=await service.RequestEmailChangeAsync(id,request.NewEmail,request.CurrentPassword,ct);return result.Succeeded?Accepted(new { message="Confirm the link sent to your new email.", verificationToken=result.DevelopmentToken }):BadRequest(new ProblemDetails{Status=400,Detail=result.ErrorCode});}
    private async Task<IActionResult> Save(int addressId,AddressRequest request,CancellationToken ct){if(!Id(out var id))return Unauthorized();var(result,errors)=await service.SaveAddressAsync(new CustomerAddress{Id=addressId,CustomerId=id,FirstName=request.FirstName,LastName=request.LastName,Company=request.Company,Address1=request.Address1,Address2=request.Address2,City=request.City,StateProvince=request.StateProvince,CountryCode=request.CountryCode,ZipPostalCode=request.ZipPostalCode,PhoneNumber=request.PhoneNumber,IsDefault=request.IsDefault},ct);return errors.Count==0?Ok(result):BadRequest(new ValidationProblemDetails(errors.ToDictionary(x=>x.Key,x=>x.Value)));}
    private bool Id(out int id)=>int.TryParse(currentUser.Subject,out id);
}

[ApiController]
[Route("api/v1/customer/email-change")]
public sealed class CustomerEmailChangeConfirmationController(ICustomerAccountDataService service) : ControllerBase
{
    [HttpGet("confirm")][AllowAnonymous] public async Task<IActionResult> Confirm([FromQuery]string token,CancellationToken ct)=>await service.ConfirmEmailChangeAsync(token,ct)?NoContent():BadRequest(new ProblemDetails{Status=400,Detail="customer.email_change_invalid"});
}

public sealed record AddressRequest(string FirstName,string LastName,string? Company,string Address1,string? Address2,string City,string? StateProvince,string CountryCode,string? ZipPostalCode,string PhoneNumber,bool IsDefault);
public sealed record EmailChangeRequest(string NewEmail,string CurrentPassword);
