using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Services.Authentication;

namespace Nomori.Marketplace.Api.Modules.Authentication;

[ApiController]
[Route("api/v1/auth")]
[EnableRateLimiting("auth")]
public sealed class AuthenticationController(
    IAuthenticationService authenticationService,
    IAuthenticationSession authenticationSession,
    ICurrentUser currentUser,
    IAntiforgery antiforgery,
    IWebHostEnvironment environment,
    IPermissionService permissionService) : ControllerBase
{
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult CsrfToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateCredentials(request.Email, request.Password);
        if (validation is not null)
            return validation;

        var result = await authenticationService.RegisterAsync(new RegisterCustomerCommand(request.Email, request.Password), cancellationToken);
        if (!result.Succeeded)
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Registration failed", Detail = result.ErrorCode });

        return Created("/api/v1/auth/session", new RegistrationResponse(result.Customer!.Id, result.Customer.Email));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateCredentials(request.Email, request.Password);
        if (validation is not null)
            return validation;

        var result = await authenticationService.LoginAsync(new LoginCustomerCommand(request.Email, request.Password), cancellationToken);
        if (!result.Succeeded)
            return Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Invalid credentials", Detail = CustomerIdentityErrors.InvalidCredentials });

        await authenticationSession.SignInAsync(result.Customer!, request.RememberMe, cancellationToken);
        return NoContent();
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await authenticationSession.SignOutAsync(cancellationToken);
        return NoContent();
    }

    [HttpGet("session")]
    [AllowAnonymous]
    public IActionResult Session()
    {
        if (!currentUser.IsAuthenticated || !int.TryParse(currentUser.Subject, out var customerId))
            return Ok(new SessionResponse(false, null, null));

        return Ok(new SessionResponse(true, customerId, currentUser.Email));
    }

    [HttpGet("permissions")]
    [Authorize]
    public async Task<IActionResult> Permissions(CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var customerId))
            return Unauthorized();

        var permissions = await permissionService.GetPermissionsAsync(customerId, cancellationToken);
        return Ok(new { permissions });
    }

    [HttpPost("password/change")]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var customerId))
            return Unauthorized();

        var error = await authenticationService.ChangePasswordAsync(new ChangePasswordCommand(customerId, request.CurrentPassword, request.NewPassword), cancellationToken);
        return error is null ? NoContent() : Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Password change failed", Detail = error });
    }

    [HttpPost("password/forgot")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var token = await authenticationService.CreatePasswordRecoveryTokenAsync(request.Email, cancellationToken);
        var response = new { message = "If the account exists, recovery instructions will be sent." };
        if (environment.IsDevelopment() && token is not null)
            return Ok(new { message = response.message, token });

        return Accepted(response);
    }

    [HttpPost("password/reset")]
    [ValidateAntiForgeryToken]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var error = await authenticationService.ResetPasswordAsync(new ResetPasswordCommand(request.Token, request.NewPassword), cancellationToken);
        return error is null ? NoContent() : Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Password reset failed", Detail = error });
    }

    private BadRequestObjectResult? ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(password))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["credentials"] = ["Email and password are required."] }));

        return null;
    }

}

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password, bool RememberMe = false);

public sealed record SessionResponse(bool IsAuthenticated, int? CustomerId, string? Email);

public sealed record RegistrationResponse(int CustomerId, string Email);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);