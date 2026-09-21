using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Services.Authentication;
using Nomori.Marketplace.Web.Framework.Security;
using Microsoft.Extensions.Options;

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
    IPermissionService permissionService,
    IPasswordPolicy passwordPolicy,
    ICustomerIdentityStore identityStore,
    IEmailVerificationService emailVerificationService,
    IEmailOtpService emailOtpService,
    IAuditLogService auditLog,
    IOptions<EmailOptions> emailOptions) : ControllerBase
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

        var passwordValidation = ValidatePassword(request.Password, passwordPolicy);
        if (passwordValidation is not null)
            return passwordValidation;

        var result = await authenticationService.RegisterAsync(new RegisterCustomerCommand(request.Email, request.Password), cancellationToken);
        if (!result.Succeeded)
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Registration failed", Detail = result.ErrorCode });

        var verificationToken = await emailVerificationService.SendVerificationAsync(result.Customer!.Id, cancellationToken);
        await auditLog.WriteAsync("auth.registered", result.Customer.Id, ipAddress: ClientIp(), cancellationToken: cancellationToken);
        return Created("/api/v1/auth/session", new RegistrationResponse(
            result.Customer.Id, result.Customer.Email, result.Customer.EmailVerified,
            environment.IsDevelopment() && !emailOptions.Value.Enabled ? verificationToken : null));
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
        {
            await auditLog.WriteAsync("auth.login_failed", details: new { reason = result.ErrorCode }, ipAddress: ClientIp(), cancellationToken: cancellationToken);
            if (result.ErrorCode == CustomerIdentityErrors.EmailNotVerified)
                return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Email verification required",
                    Detail = CustomerIdentityErrors.EmailNotVerified
                });

            return Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Invalid credentials", Detail = CustomerIdentityErrors.InvalidCredentials });
        }

        if (result.Customer!.EmailOtpEnabled)
        {
            var otp = await emailOtpService.CreateAsync(result.Customer.Id, "login", cancellationToken);
            if (!otp.Succeeded)
                return OtpFailure(otp.ErrorCode!);

            return Accepted(new LoginOtpRequiredResponse(otp.ChallengeId, otp.ExpiresOnUtc,
                environment.IsDevelopment() ? otp.DevelopmentCode : null));
        }

        await authenticationSession.SignInAsync(result.Customer!, request.RememberMe, cancellationToken);
        await auditLog.WriteAsync("auth.login_succeeded", result.Customer.Id, ipAddress: ClientIp(), cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpPost("logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        var customerId = int.TryParse(currentUser.Subject, out var parsedCustomerId) ? parsedCustomerId : (int?)null;
        await authenticationSession.SignOutAsync(cancellationToken);
        await auditLog.WriteAsync("auth.logout", customerId, ipAddress: ClientIp(), cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<IActionResult> Session(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || !int.TryParse(currentUser.Subject, out var customerId))
            return Ok(new SessionResponse(false, null, null, null, null));

        var customer = await identityStore.FindByIdAsync(customerId, cancellationToken);
        return Ok(new SessionResponse(true, customerId, currentUser.Email, customer?.EmailVerified, customer?.EmailOtpEnabled));
    }

    [HttpGet("permissions")]
    [HasPermission(PermissionCodes.PermissionsRead)]
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
        if (error is null)
        {
            await authenticationSession.SignOutAsync(cancellationToken);
            await auditLog.WriteAsync("auth.password_changed", customerId, ipAddress: ClientIp(), cancellationToken: cancellationToken);
            return NoContent();
        }

        return error == CustomerIdentityErrors.PasswordPolicy || error == CustomerIdentityErrors.PasswordRecentlyUsed
            ? PasswordValidationFailure(error)
            : Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Password change failed", Detail = error });
    }

    [HttpPost("password/forgot")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        var token = await authenticationService.CreatePasswordRecoveryTokenAsync(request.Email, cancellationToken);
        await auditLog.WriteAsync("auth.password_recovery_requested", ipAddress: ClientIp(), cancellationToken: cancellationToken);
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
        if (error is null)
        {
            await auditLog.WriteAsync("auth.password_reset", ipAddress: ClientIp(), cancellationToken: cancellationToken);
            return NoContent();
        }

        return error == CustomerIdentityErrors.PasswordPolicy || error == CustomerIdentityErrors.PasswordRecentlyUsed
            ? PasswordValidationFailure(error)
            : Unauthorized(new ProblemDetails { Status = StatusCodes.Status401Unauthorized, Title = "Password reset failed", Detail = error });
    }

    [HttpPost("email/verification/send")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendEmailVerification(EmailVerificationRequest request, CancellationToken cancellationToken)
    {
        var customer = await identityStore.FindByEmailAsync(request.Email.Trim().ToLowerInvariant(), cancellationToken);
        var token = customer is null ? null : await emailVerificationService.SendVerificationAsync(customer.Id, cancellationToken);
        var response = new { message = "If the account exists and is not verified, verification instructions will be sent." };
        if (environment.IsDevelopment() && token is not null)
            return Ok(new { response.message, token });

        return Accepted(response);
    }

    [HttpGet("email/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyEmail([FromQuery] string token, CancellationToken cancellationToken)
    {
        var verified = await emailVerificationService.VerifyAsync(token, cancellationToken);
        return verified
            ? NoContent()
            : BadRequest(new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "Email verification failed", Detail = CustomerIdentityErrors.EmailVerificationInvalid });
    }

    [HttpPost("login/otp/verify")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyLoginOtp(VerifyLoginOtpRequest request, CancellationToken cancellationToken)
    {
        var result = await emailOtpService.VerifyAsync(request.ChallengeId, request.Code, "login", cancellationToken);
        if (!result.Succeeded)
            return OtpFailure(result.ErrorCode!);

        await authenticationSession.SignInAsync(result.Customer!, request.RememberMe, cancellationToken);
        await auditLog.WriteAsync("auth.login_succeeded", result.Customer!.Id, ipAddress: ClientIp(), cancellationToken: cancellationToken);
        return NoContent();
    }

    [HttpPost("otp/setup")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetupOtp(CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var customerId))
            return Unauthorized();

        var result = await emailOtpService.CreateAsync(customerId, "enable", cancellationToken);
        if (!result.Succeeded)
            return OtpFailure(result.ErrorCode!);

        return Ok(new OtpChallengeResponse(result.ChallengeId, result.ExpiresOnUtc,
            environment.IsDevelopment() ? result.DevelopmentCode : null));
    }

    [HttpPost("otp/enable")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EnableOtp(ConfirmOtpRequest request, CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var customerId))
            return Unauthorized();

        var result = await emailOtpService.VerifyAsync(request.ChallengeId, request.Code, "enable", cancellationToken);
        if (!result.Succeeded || result.Customer?.Id != customerId)
            return OtpFailure(result.ErrorCode ?? CustomerIdentityErrors.OtpInvalid);

        await emailOtpService.SetEnabledAsync(customerId, true, cancellationToken);
        return NoContent();
    }

    [HttpPost("otp/disable")]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisableOtp(CancellationToken cancellationToken)
    {
        if (!int.TryParse(currentUser.Subject, out var customerId))
            return Unauthorized();

        await emailOtpService.SetEnabledAsync(customerId, false, cancellationToken);
        return NoContent();
    }

    [HttpGet("password/policy")]
    [AllowAnonymous]
    public IActionResult PasswordPolicy() => Ok(passwordPolicy.Requirements);

    private BadRequestObjectResult? ValidateCredentials(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(password))
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]> { ["credentials"] = ["Email and password are required."] }));

        return null;
    }

    private static BadRequestObjectResult? ValidatePassword(string password, IPasswordPolicy policy)
    {
        var errors = policy.Validate(password);
        if (errors.Count == 0)
            return null;

        var problem = new ValidationProblemDetails(new Dictionary<string, string[]> { ["password"] = errors.ToArray() });
        problem.Extensions["code"] = CustomerIdentityErrors.PasswordPolicy;
        return new BadRequestObjectResult(problem);
    }

    private BadRequestObjectResult OtpFailure(string errorCode) =>
        BadRequest(new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "OTP verification failed", Detail = errorCode });

    private static BadRequestObjectResult PasswordValidationFailure(string errorCode)
    {
        var problem = new ValidationProblemDetails(new Dictionary<string, string[]> { ["password"] = [errorCode] })
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Password validation failed",
            Detail = errorCode
        };
        problem.Extensions["code"] = errorCode;
        return new BadRequestObjectResult(problem);
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

}

public sealed record RegisterRequest(string Email, string Password);

public sealed record LoginRequest(string Email, string Password, bool RememberMe = false);

public sealed record SessionResponse(bool IsAuthenticated, int? CustomerId, string? Email, bool? EmailVerified, bool? EmailOtpEnabled);

public sealed record RegistrationResponse(int CustomerId, string Email, bool EmailVerified, string? VerificationToken);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);

public sealed record EmailVerificationRequest(string Email);

public sealed record LoginOtpRequiredResponse(Guid ChallengeId, DateTime ExpiresOnUtc, string? DevelopmentCode);

public sealed record OtpChallengeResponse(Guid ChallengeId, DateTime ExpiresOnUtc, string? DevelopmentCode);

public sealed record VerifyLoginOtpRequest(Guid ChallengeId, string Code, bool RememberMe = false);

public sealed record ConfirmOtpRequest(Guid ChallengeId, string Code);
