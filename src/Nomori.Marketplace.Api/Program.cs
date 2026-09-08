using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Nomori.Marketplace.Api.Configuration;
using Nomori.Marketplace.Api.Health;
using Nomori.Marketplace.Api.Middleware;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Data.Configuration;
using Nomori.Marketplace.Web.Framework.Security;
using Nomori.Marketplace.Services.Authentication;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Services.Security;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole();
builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestProperties
        | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.Duration;
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "Nomori.Csrf";
    options.Cookie.HttpOnly = false;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddOptions<ApplicationOptions>()
    .Bind(builder.Configuration.GetSection(ApplicationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName));

builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi();
builder.Services.AddNomoriData(builder.Configuration);
builder.Services.AddNomoriSecurity(builder.Configuration);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddSingleton<Nomori.Marketplace.Services.ApplicationInfo.IApplicationInfoService, Nomori.Marketplace.Services.ApplicationInfo.ApplicationInfoService>();
builder.Services.AddNomoriHealthChecks();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        var corsOptions = builder.Configuration.GetSection(CorsOptions.SectionName).Get<CorsOptions>() ?? new CorsOptions();
        if (corsOptions.AllowedOrigins.Length > 0)
            policy.WithOrigins(corsOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseHttpLogging();
app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseRateLimiter();
app.UseNomoriSecurity();
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthChecks.LiveTag)
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthChecks.ReadyTag)
});

app.Run();
