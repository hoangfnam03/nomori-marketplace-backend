using System.Text.Encodings.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Customers;
using Nomori.Marketplace.Core.Email;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;
using Nomori.Marketplace.Core.Vendors;

namespace Nomori.Marketplace.Services.Vendors;

public sealed partial class VendorApplicationService(
    IVendorApplicationStore applicationStore,
    ICustomerIdentityStore customerStore,
    IEmailSender emailSender,
    IAuditLogService auditLog,
    IClock clock,
    IOptions<EmailOptions> emailOptions,
    ILogger<VendorApplicationService> logger) : IVendorApplicationService
{
    private const int MaxReasonLength = 2000;

    public async Task<VendorResult<VendorApplication>> GetAsync(int id, VendorCaller caller, CancellationToken cancellationToken)
    {
        var application = await GetVisibleAsync(id, caller, cancellationToken);
        return application is null
            ? VendorResult.Error<VendorApplication>(VendorErrors.NotFound)
            : VendorResult.Success(application);
    }

    public async Task<PagedResult<VendorApplication>> GetListAsync(
        VendorApplicationQuery query, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.IsAuthenticated)
            return new PagedResult<VendorApplication>([], 0, query.Page, query.PageSize);

        // Customers only ever see their own applications and cannot search; admins see everything.
        var scoped = caller.IsAdmin
            ? query with { OldestFirst = query.Status == VendorApplicationStatus.Pending }
            : query with { CustomerId = caller.CustomerId, Search = null, OldestFirst = false };

        var (items, total) = await applicationStore.GetPagedAsync(scoped, cancellationToken);
        return new PagedResult<VendorApplication>(items, total, scoped.Page, scoped.PageSize);
    }

    public async Task<VendorResult<VendorApplication>> SubmitAsync(
        SubmitVendorApplicationCommand command, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (caller.CustomerId is not { } customerId)
            return VendorResult.Error<VendorApplication>(VendorErrors.Forbidden);

        var customer = await customerStore.FindByIdAsync(customerId, cancellationToken);
        if (customer is null || customer.Deleted)
            return VendorResult.Error<VendorApplication>(VendorErrors.Forbidden);
        if (!customer.EmailVerified)
            return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationEmailNotVerified);
        if (caller.MemberVendorId is not null)
            return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationAlreadyVendor);

        var (application, errors) = await BuildContentAsync(
            command.ShopName, command.Email, command.PhoneNumber, command.Description, command.TaxCode, command.BusinessAddress,
            excludeApplicationId: null, cancellationToken);
        if (errors.Count > 0) return VendorResult.Failure<VendorApplication>(errors);

        var now = clock.UtcNow;
        application.CustomerId = customerId;
        application.Status = VendorApplicationStatus.Pending;
        application.CreatedOnUtc = now;
        application.UpdatedOnUtc = now;

        var id = await applicationStore.InsertAsync(application, cancellationToken);
        if (id is null) return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationAlreadyPending);

        await auditLog.WriteAsync("vendor.application_submitted", customerId, entityType: "VendorApplication", entityId: id,
            details: new { applicationId = id }, cancellationToken: cancellationToken);

        var saved = await applicationStore.GetAsync(id.Value, cancellationToken);
        return VendorResult.Success(saved ?? application);
    }

    public async Task<VendorResult<VendorApplication>> UpdateAsync(
        int id, UpdateVendorApplicationCommand command, VendorCaller caller, CancellationToken cancellationToken)
    {
        var existing = await GetVisibleAsync(id, caller, cancellationToken);
        if (existing is null) return VendorResult.Error<VendorApplication>(VendorErrors.NotFound);
        if (existing.CustomerId != caller.CustomerId) return VendorResult.Error<VendorApplication>(VendorErrors.Forbidden);
        if (existing.Status != VendorApplicationStatus.Pending) return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationNotPending);

        var (updated, errors) = await BuildContentAsync(
            command.ShopName, command.Email, command.PhoneNumber, command.Description, command.TaxCode, command.BusinessAddress,
            excludeApplicationId: id, cancellationToken);
        if (errors.Count > 0) return VendorResult.Failure<VendorApplication>(errors);

        var changedFields = new List<string>();
        if (existing.ShopName != updated.ShopName) changedFields.Add("shopName");
        if (existing.Email != updated.Email) changedFields.Add("email");
        if (existing.PhoneNumber != updated.PhoneNumber) changedFields.Add("phoneNumber");
        if (existing.Description != updated.Description) changedFields.Add("description");
        if (existing.TaxCode != updated.TaxCode) changedFields.Add("taxCode");
        if (existing.BusinessAddress != updated.BusinessAddress) changedFields.Add("businessAddress");

        existing.ShopName = updated.ShopName;
        existing.Email = updated.Email;
        existing.PhoneNumber = updated.PhoneNumber;
        existing.Description = updated.Description;
        existing.TaxCode = updated.TaxCode;
        existing.BusinessAddress = updated.BusinessAddress;
        existing.UpdatedOnUtc = clock.UtcNow;

        // The row may have been reviewed between the read and the write; the store checks Status = pending.
        if (!await applicationStore.UpdateContentAsync(existing, cancellationToken))
            return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationNotPending);

        await auditLog.WriteAsync("vendor.application_updated", caller.CustomerId, entityType: "VendorApplication", entityId: id,
            details: new { applicationId = id, changedFields }, cancellationToken: cancellationToken);
        return VendorResult.Success(existing);
    }

    public async Task<VendorResult<VendorApplication>> ChangeStatusAsync(
        int id, ChangeVendorApplicationStatusCommand command, VendorCaller caller, CancellationToken cancellationToken)
    {
        var application = await GetVisibleAsync(id, caller, cancellationToken);
        if (application is null) return VendorResult.Error<VendorApplication>(VendorErrors.NotFound);

        var target = command.Status;
        if (target is null or VendorApplicationStatus.Pending)
            return VendorResult.Failure<VendorApplication>("status", "Status must be approved, rejected or cancelled.");

        var errors = new Dictionary<string, string[]>();
        var reason = VendorValidation.NullIfBlank(command.Reason);
        if (target == VendorApplicationStatus.Rejected)
        {
            if (reason is null) errors["reason"] = ["A reason is required when rejecting an application."];
            else if (reason.Length > MaxReasonLength) errors["reason"] = [$"Reason cannot exceed {MaxReasonLength} characters."];
        }
        else if (reason is not null)
        {
            errors["reason"] = ["A reason is only allowed when rejecting an application."];
        }
        if (errors.Count > 0) return VendorResult.Failure<VendorApplication>(errors);

        var isApplicant = application.CustomerId == caller.CustomerId;
        var allowed = target == VendorApplicationStatus.Cancelled ? isApplicant : caller.IsAdmin;
        if (!allowed) return VendorResult.Error<VendorApplication>(VendorErrors.Forbidden);

        if (application.Status != VendorApplicationStatus.Pending)
            return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationNotPending);

        return target switch
        {
            VendorApplicationStatus.Approved => await ApproveAsync(application, command, caller, cancellationToken),
            VendorApplicationStatus.Rejected => await RejectAsync(application, reason!, caller, cancellationToken),
            _ => await CancelAsync(application, caller, cancellationToken)
        };
    }

    private async Task<VendorResult<VendorApplication>> ApproveAsync(
        VendorApplication application, ChangeVendorApplicationStatusCommand command, VendorCaller caller, CancellationToken cancellationToken)
    {
        var shopName = VendorValidation.NullIfBlank(command.ShopName) ?? application.ShopName;
        var errors = new Dictionary<string, string[]>();
        VendorValidation.ValidateShopName(shopName, errors);
        if (errors.Count == 0 && await applicationStore.ShopNameExistsAsync(shopName, application.Id, cancellationToken))
            errors["shopName"] = ["Shop name is already in use."];
        if (errors.Count > 0) return VendorResult.Failure<VendorApplication>(errors);

        var adminComment = VendorValidation.NullIfBlank(command.AdminComment);
        var result = await applicationStore.ApproveAsync(
            application.Id, shopName, adminComment, caller.CustomerId!.Value, clock.UtcNow, cancellationToken);

        switch (result.Outcome)
        {
            case ApproveApplicationOutcome.NotPending:
                return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationNotPending);
            case ApproveApplicationOutcome.ApplicantAlreadyVendor:
                return VendorResult.Error<VendorApplication>(VendorErrors.ApplicantAlreadyVendor);
        }

        await auditLog.WriteAsync("vendor.application_approved", caller.CustomerId, application.CustomerId,
            "VendorApplication", application.Id,
            details: new { applicationId = application.Id, vendorId = result.VendorId, reviewer = caller.CustomerId },
            cancellationToken: cancellationToken);

        await SendAsync(application.CustomerId, emailOptions.Value.VendorApplicationApprovedSubject,
            _ => "<p>Congratulations, your shop application was approved.</p>",
            link: "/vendor", linkText: "Open your vendor portal", cancellationToken);

        var saved = await applicationStore.GetAsync(application.Id, cancellationToken);
        return VendorResult.Success(saved ?? application);
    }

    private async Task<VendorResult<VendorApplication>> RejectAsync(
        VendorApplication application, string reason, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (!await applicationStore.CloseAsync(application.Id, VendorApplicationStatus.Rejected, reason, caller.CustomerId, clock.UtcNow, cancellationToken))
            return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationNotPending);

        await auditLog.WriteAsync("vendor.application_rejected", caller.CustomerId, application.CustomerId,
            "VendorApplication", application.Id,
            details: new { applicationId = application.Id, reviewer = caller.CustomerId },
            cancellationToken: cancellationToken);

        await SendAsync(application.CustomerId, emailOptions.Value.VendorApplicationRejectedSubject,
            encoder => $"<p>Your shop application was not approved.</p><p>Reason: {encoder.Encode(reason)}</p>",
            link: "/customer/become-vendor", linkText: "Apply again", cancellationToken);

        var saved = await applicationStore.GetAsync(application.Id, cancellationToken);
        return VendorResult.Success(saved ?? application);
    }

    private async Task<VendorResult<VendorApplication>> CancelAsync(
        VendorApplication application, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (!await applicationStore.CloseAsync(application.Id, VendorApplicationStatus.Cancelled, null, null, clock.UtcNow, cancellationToken))
            return VendorResult.Error<VendorApplication>(VendorErrors.ApplicationNotPending);

        await auditLog.WriteAsync("vendor.application_cancelled", caller.CustomerId, entityType: "VendorApplication",
            entityId: application.Id, details: new { applicationId = application.Id }, cancellationToken: cancellationToken);

        var saved = await applicationStore.GetAsync(application.Id, cancellationToken);
        return VendorResult.Success(saved ?? application);
    }

    /// <summary>Applicants see only their own applications; admins see all. Anyone else gets "not found".</summary>
    private async Task<VendorApplication?> GetVisibleAsync(int id, VendorCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.IsAuthenticated) return null;
        var application = await applicationStore.GetAsync(id, cancellationToken);
        if (application is null) return null;
        return caller.IsAdmin || application.CustomerId == caller.CustomerId ? application : null;
    }

    private async Task<(VendorApplication Application, Dictionary<string, string[]> Errors)> BuildContentAsync(
        string? shopName, string? email, string? phoneNumber, string? description, string? taxCode, string? businessAddress,
        int? excludeApplicationId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var normalizedEmail = VendorValidation.NormalizeEmail(email);
        VendorValidation.ValidateShopName(shopName, errors);
        VendorValidation.ValidateEmail(normalizedEmail, errors);
        VendorValidation.ValidatePhone(phoneNumber, errors);
        VendorValidation.ValidateMaxLength(taxCode, 50, "taxCode", "Tax code", errors);
        VendorValidation.ValidateMaxLength(businessAddress, 1000, "businessAddress", "Business address", errors);

        var name = shopName?.Trim() ?? string.Empty;
        if (!errors.ContainsKey("shopName") && await applicationStore.ShopNameExistsAsync(name, excludeApplicationId, cancellationToken))
            errors["shopName"] = ["Shop name is already in use."];

        var application = new VendorApplication
        {
            ShopName = name,
            Email = normalizedEmail,
            PhoneNumber = phoneNumber?.Trim() ?? string.Empty,
            Description = VendorValidation.NullIfBlank(description),
            TaxCode = VendorValidation.NullIfBlank(taxCode),
            BusinessAddress = VendorValidation.NullIfBlank(businessAddress)
        };
        return (application, errors);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to send vendor application email to customer {CustomerId}.")]
    private partial void LogEmailFailed(Exception exception, int customerId);

    /// <summary>Sends a notification to the applicant. A delivery failure is logged and never undoes the review.</summary>
    private async Task SendAsync(
        int customerId, string subject, Func<HtmlEncoder, string> body, string link, string linkText, CancellationToken cancellationToken)
    {
        if (!emailOptions.Value.Enabled) return;

        try
        {
            var customer = await customerStore.FindByIdAsync(customerId, cancellationToken);
            if (customer is null) return;

            var baseUrl = emailOptions.Value.FrontendBaseUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("Email:FrontendBaseUrl must be configured when email delivery is enabled.");

            var url = baseUrl + link;
            var encoder = HtmlEncoder.Default;
            await emailSender.SendEmailAsync(new EmailMessage(
                customer.Email,
                subject,
                $"{body(encoder)}<p><a href=\"{encoder.Encode(url)}\">{encoder.Encode(linkText)}</a></p>",
                $"{linkText}: {url}"), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogEmailFailed(ex, customerId);
        }
    }
}
