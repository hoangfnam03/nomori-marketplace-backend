using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Nomori.Marketplace.Core.Security;

namespace Nomori.Marketplace.Web.Framework.Security;

public sealed class NomoriCookieValidationEvents(
    ICurrentUserValidator currentUserValidator) : CookieAuthenticationEvents
{
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var subject = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(subject, out var customerId)
            || !await currentUserValidator.IsValidAsync(customerId, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync("NomoriCookie");
        }
    }
}
