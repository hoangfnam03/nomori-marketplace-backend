using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.OpenApi;
using Nomori.Marketplace.Api.Configuration;
using Nomori.Marketplace.Api.Health;
using Nomori.Marketplace.Api.Middleware;
using Nomori.Marketplace.Core.Cart;
using Nomori.Marketplace.Core.Checkout;
using Nomori.Marketplace.Core.Discounts;
using Nomori.Marketplace.Core.Orders;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Shipping;
using Nomori.Marketplace.Core.Tax;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Configuration;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Media;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;
using Nomori.Marketplace.Data.Configuration;
using Nomori.Marketplace.Services.Authentication;
using Nomori.Marketplace.Services.Cart;
using Nomori.Marketplace.Services.Checkout;
using Nomori.Marketplace.Services.Discounts;
using Nomori.Marketplace.Services.Orders;
using Nomori.Marketplace.Services.Payments;
using Nomori.Marketplace.Services.Shipping;
using Nomori.Marketplace.Services.Tax;
using Nomori.Marketplace.Services.Catalog;
using Nomori.Marketplace.Services.Customers;
using Nomori.Marketplace.Services.Directory;
using Nomori.Marketplace.Services.Email;
using Nomori.Marketplace.Services.Media;
using Nomori.Marketplace.Services.Security;
using Nomori.Marketplace.Services.Vendors;
using Nomori.Marketplace.Web.Framework.Security;
using Nomori.Marketplace.Web.Framework.Vendors;

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
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.None
        : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Gateways call from a few addresses and retry, so the limit is higher than for people; the signature is what protects the route.
    options.AddPolicy("callbacks", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddOptions<ApplicationOptions>()
    .Bind(builder.Configuration.GetSection(ApplicationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.AddOptions<CorsOptions>()
    .Bind(builder.Configuration.GetSection(CorsOptions.SectionName));
builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddOptions<MediaOptions>()
    .Bind(builder.Configuration.GetSection(MediaOptions.SectionName))
    .Validate(options => options.MaxUploadBytes is > 0 and <= 20 * 1024 * 1024, "Media:MaxUploadBytes must be between 1 byte and 20 MiB.")
    .ValidateOnStart();
builder.Services.AddOptions<MediaStorageOptions>()
    .Bind(builder.Configuration.GetSection(MediaStorageOptions.SectionName))
    .Validate(options => options.UsesObjectStorage || string.Equals(options.Provider, "Database", StringComparison.OrdinalIgnoreCase),
        "Media:Storage:Provider must be Database or S3.")
    .Validate(options => !options.UsesObjectStorage
        || (Uri.TryCreate(options.S3.Endpoint, UriKind.Absolute, out _)
            && (string.IsNullOrWhiteSpace(options.S3.PublicEndpoint) || Uri.TryCreate(options.S3.PublicEndpoint, UriKind.Absolute, out _))
            && !string.IsNullOrWhiteSpace(options.S3.Bucket)
            && !string.IsNullOrWhiteSpace(options.S3.AccessKey)
            && !string.IsNullOrWhiteSpace(options.S3.SecretKey)
            && options.S3.UploadUrlLifetimeMinutes is > 0 and <= 60),
        "Media:Storage:S3 needs absolute Endpoint (and PublicEndpoint if set), Bucket, AccessKey, SecretKey and UploadUrlLifetimeMinutes between 1 and 60.")
    .ValidateOnStart();
builder.Services.AddOptions<VendorOptions>()
    .Bind(builder.Configuration.GetSection(VendorOptions.SectionName))
    .Validate(options => options.MaxMembersPerVendor >= 1, "Vendor:MaxMembersPerVendor must be at least 1.")
    .ValidateOnStart();

builder.Services.AddControllersWithViews();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["csrfToken"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            Name = "X-CSRF-TOKEN",
            In = ParameterLocation.Header,
            Description = "Enter the token returned by GET /api/v1/auth/csrf. The Nomori.Csrf cookie is also required."
        };

        var csrfPaths = new HashSet<string>
        {
            "/api/v1/auth/register",
            "/api/v1/auth/login",
            "/api/v1/auth/logout",
            "/api/v1/auth/password/change",
            "/api/v1/auth/password/forgot",
            "/api/v1/auth/password/reset",
            "/api/v1/auth/email/verification/send",
            "/api/v1/auth/login/otp/verify",
            "/api/v1/auth/otp/setup",
            "/api/v1/auth/otp/enable",
            "/api/v1/auth/otp/disable",
            "/api/v1/admin/authorization/customers/{customerId}/roles",
            "/api/v1/customer/profile"
            ,"/api/v1/customer/addresses", "/api/v1/customer/attributes", "/api/v1/customer/email-change/request"
            ,"/api/v1/media", "/api/v1/media/{id}", "/api/v1/media/uploads", "/api/v1/media/uploads/{id}/complete", "/api/v1/vendors/{vendorId}/products", "/api/v1/vendors/{vendorId}/products/{id}", "/api/v1/admin/catalog/products/{id}/transfer", "/api/v1/admin/catalog/products/{id}/hide", "/api/v1/admin/catalog/products/{id}/unhide", "/api/v1/vendors/{vendorId}/products/{id}/status", "/api/v1/vendors/{vendorId}/products/{id}/review-request", "/api/v1/vendor-applications", "/api/v1/vendor-applications/{id}", "/api/v1/vendor-applications/{id}/status",
            "/api/v1/vendors/{id}", "/api/v1/vendors/{id}/members", "/api/v1/vendors/{id}/members/{customerId}",
            "/api/v1/vendors/{id}/members/{customerId}/setup-email", "/api/v1/vendors/{id}/notes", "/api/v1/vendors/{id}/notes/{noteId}"
        };
        foreach (var (path, pathItem) in document.Paths)
        {
            if (!csrfPaths.Contains(path))
                continue;

            if (pathItem.Operations is null)
                continue;

            foreach (var operation in pathItem.Operations.Values)
            {
                operation.Parameters ??= [];
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "X-CSRF-TOKEN",
                    In = ParameterLocation.Header,
                    Required = true,
                    Description = "Token returned by GET /api/v1/auth/csrf. The Nomori.Csrf cookie is also required."
                });
            }
        }

        return Task.CompletedTask;
    });
});
builder.Services.AddNomoriData(builder.Configuration);
builder.Services.AddNomoriSecurity(builder.Configuration, builder.Environment);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IPasswordPolicy, PasswordPolicy>();
builder.Services.AddScoped<IAuthenticationService, AuthenticationService>();
builder.Services.AddScoped<IEmailVerificationService, EmailVerificationService>();
builder.Services.AddScoped<IEmailOtpService, EmailOtpService>();
builder.Services.AddScoped<ICurrentUserValidator, CurrentUserValidator>();
builder.Services.AddScoped<ISmtpBuilder, SmtpBuilder>();
builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IAuthorizationManagementService, AuthorizationManagementService>();
builder.Services.AddScoped<IAuditLogService, AuditLogService>();
builder.Services.AddScoped<ICustomerProfileService, CustomerProfileService>();
builder.Services.AddScoped<ICustomerAccountDataService, CustomerAccountDataService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IManufacturerService, ManufacturerService>();
builder.Services.AddScoped<ITaxonomyService, TaxonomyService>();
builder.Services.AddScoped<IProductService, ProductService>();
builder.Services.AddScoped<IProductAttributeService, ProductAttributeService>();
builder.Services.AddScoped<ISpecificationAttributeService, SpecificationAttributeService>();
builder.Services.AddScoped<IVendorProductDetailsService, VendorProductDetailsService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<ICatalogSearchService, CatalogSearchService>();
builder.Services.AddScoped<ICurrencyService, CurrencyService>();
builder.Services.AddScoped<IDirectoryService, DirectoryService>();
builder.Services.AddScoped<IPriceCalculationService, PriceCalculationService>();
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IShippingService, ShippingService>();
builder.Services.AddOptions<PaymentOptions>().Bind(builder.Configuration.GetSection(PaymentOptions.SectionName));
builder.Services.AddSingleton<IPaymentProvider, CashOnDeliveryProvider>();
// The test gateway only exists where a secret is configured, so production cannot offer it by accident.
var sandboxPayments = builder.Configuration.GetSection($"{PaymentOptions.SectionName}:Sandbox").Get<SandboxPaymentOptions>();
if (sandboxPayments is { IsConfigured: true })
    builder.Services.AddSingleton<IPaymentProvider>(new SandboxPaymentProvider(sandboxPayments.Secret));
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<IDiscountService, DiscountService>();
builder.Services.AddScoped<ITaxService, TaxService>();
builder.Services.AddScoped<ICheckoutService, CheckoutService>();
builder.Services.AddScoped<IProductPricingService, ProductPricingService>();
builder.Services.AddScoped<IPrimaryCurrencyProvider, PrimaryCurrencyProvider>();
builder.Services.AddScoped<IVendorService, VendorService>();
builder.Services.AddScoped<IVendorApplicationService, VendorApplicationService>();
builder.Services.AddScoped<IVendorMemberService, VendorMemberService>();
builder.Services.AddScoped<IVendorAccessContext, VendorAccessContext>();
builder.Services.AddScoped<IMediaService, MediaService>();
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
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Nomori Marketplace API v1");
    });
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
