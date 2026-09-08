using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Domain.Customers;

namespace Nomori.Marketplace.Web.Framework.Security;

public static class NomoriSecurityExtensions
{
    public static IServiceCollection AddNomoriSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityOptions>()
            .Bind(configuration.GetSection(SecurityOptions.SectionName));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddScoped<IAuthenticationSession, HttpAuthenticationSession>();
        services.AddDataProtection();
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "NomoriCookie";
                options.DefaultChallengeScheme = "NomoriCookie";
                options.DefaultSignInScheme = "NomoriCookie";
            })
            .AddCookie("NomoriCookie", options =>
            {
                var securityOptions = configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
                options.Cookie.Name = securityOptions.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.LoginPath = securityOptions.LoginPath;
                options.AccessDeniedPath = securityOptions.AccessDeniedPath;
                options.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization();

        return services;
    }

    public static WebApplication UseNomoriSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    private sealed class HttpCurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
    {
        public bool IsAuthenticated => httpContextAccessor.HttpContext?.User.Identity?.IsAuthenticated == true;

        public string? Subject => httpContextAccessor.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        public string? Email => httpContextAccessor.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
    }

    private sealed class HttpAuthenticationSession(IHttpContextAccessor httpContextAccessor) : IAuthenticationSession
    {
        public async Task SignInAsync(Customer customer, bool rememberMe, CancellationToken cancellationToken)
        {
            var claims = new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, customer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, customer.Email)
            };
            var identity = new System.Security.Claims.ClaimsIdentity(claims, "NomoriCookie");
            await httpContextAccessor.HttpContext!.SignInAsync("NomoriCookie", new System.Security.Claims.ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                IssuedUtc = DateTimeOffset.UtcNow
            });
        }

        public Task SignOutAsync(CancellationToken cancellationToken)
        {
            return httpContextAccessor.HttpContext!.SignOutAsync("NomoriCookie");
        }
    }
}