using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Services.Authentication;

namespace Nomori.Marketplace.Api.Modules.Authentication;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthenticationController(
    IAuthenticationService authenticationService,
    IAuthenticationSession authenticationSession,
    ICurrentUser currentUser) : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
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