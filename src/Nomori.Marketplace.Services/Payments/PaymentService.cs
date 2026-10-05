using Nomori.Marketplace.Core.Catalog;
using Nomori.Marketplace.Core.Directory;
using Nomori.Marketplace.Core.Payments;
using Nomori.Marketplace.Core.Security;
using Nomori.Marketplace.Core.Time;

namespace Nomori.Marketplace.Services.Payments;

public sealed class PaymentService(
    IPaymentStore store,
    IEnumerable<IPaymentProvider> providers,
    IEnumerable<IPaymentOutcomeHandler> handlers,
    IPrimaryCurrencyProvider primaryCurrency,
    IAuditLogService auditLog,
    IClock clock) : IPaymentService
{
    private const string Applied = "applied";
    private const string Ignored = "ignored";

    private readonly Dictionary<string, IPaymentProvider> registered =
        providers.ToDictionary(p => p.SystemName, StringComparer.OrdinalIgnoreCase);

    // ---- Customers ----

    public async Task<IReadOnlyList<PaymentMethodView>> GetAvailableMethodsAsync(CancellationToken cancellationToken) =>
        (await GetMethodsAsync(cancellationToken)).Where(m => m.Enabled && m.Registered).ToList();

    // ---- Checkout ----

    public async Task<CatalogResult<PaymentTransaction>> CreateAsync(CreatePaymentCommand command, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var referenceType = command.ReferenceType?.Trim() ?? string.Empty;
        var key = command.IdempotencyKey?.Trim() ?? string.Empty;
        if (referenceType.Length is 0 or > PaymentLimits.MaxReferenceTypeLength) errors["referenceType"] = ["Name what is being paid."];
        if (command.ReferenceId <= 0) errors["referenceId"] = ["Name what is being paid."];
        if (key.Length is 0 or > PaymentLimits.MaxIdempotencyKeyLength)
            errors["idempotencyKey"] = [$"The key must have 1 to {PaymentLimits.MaxIdempotencyKeyLength} characters."];

        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        if (command.Amount <= 0 || command.Amount > PaymentLimits.MaxAmount)
            errors["amount"] = [$"The amount must be above 0 and at most {PaymentLimits.MaxAmount:0}."];
        else if (!CurrencyRules.HasValidScale(command.Amount, currency.DecimalPlaces))
            errors["amount"] = [$"The amount can have at most {currency.DecimalPlaces} decimal places."];

        var method = command.Method?.Trim() ?? string.Empty;
        if (method.Length == 0) errors["method"] = ["Choose a payment method."];
        if (errors.Count > 0) return CatalogResult.Failure<PaymentTransaction>(errors);

        // The same key means the same request again: answer with what it created, but only when nothing differs.
        var existing = await store.GetByKeyAsync(key, cancellationToken);
        if (existing is not null) return Repeat(existing, referenceType, command, method);

        if (!registered.TryGetValue(method, out var provider) || !await IsEnabledAsync(provider.SystemName, cancellationToken))
            return CatalogResult.Error<PaymentTransaction>(PaymentErrors.MethodUnavailable);

        var now = clock.UtcNow;
        var payment = new PaymentTransaction
        {
            ReferenceType = referenceType, ReferenceId = command.ReferenceId, IdempotencyKey = key, Method = provider.SystemName,
            CustomerId = command.CustomerId, Amount = command.Amount, CurrencyCode = currency.Code, Status = PaymentStatus.Pending,
            CreatedOnUtc = now, UpdatedOnUtc = now
        };
        payment.Id = await store.InsertAsync(payment, cancellationToken);
        if (payment.Id == 0)
        {
            // Two requests with the same key arrived together; the other one won.
            var winner = await store.GetByKeyAsync(key, cancellationToken);
            return winner is null ? CatalogResult.Error<PaymentTransaction>(PaymentErrors.IdempotencyConflict) : Repeat(winner, referenceType, command, method);
        }

        var started = await provider.InitiateAsync(payment, cancellationToken);
        if (!started.Succeeded)
        {
            await store.TryChangeAsync(payment.Id, PaymentStatus.Pending, PaymentStatus.Failed, 0, null, started.FailureCode, clock.UtcNow, cancellationToken);
            await AuditAsync("payment.failed", null, payment, new { payment.Id, payment.Method, failureCode = started.FailureCode }, cancellationToken);
            return CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed);
        }

        var status = provider.Kind == PaymentProviderKind.Gateway ? PaymentStatus.Authorized : PaymentStatus.Pending;
        await store.TryChangeAsync(payment.Id, PaymentStatus.Pending, status, 0, started.ProviderReference, null, clock.UtcNow, cancellationToken);

        await AuditAsync("payment.created", command.CustomerId, payment,
            new { payment.Id, payment.ReferenceType, payment.ReferenceId, payment.Method, payment.Amount, payment.CurrencyCode }, cancellationToken);
        var created = (await store.GetAsync(payment.Id, cancellationToken))!;
        created.RedirectUrl = provider.GetRedirectUrl(created);
        return CatalogResult.Success(created);
    }

    // ---- Administrators ----

    public async Task<IReadOnlyList<PaymentMethodView>> GetMethodsAsync(CancellationToken cancellationToken)
    {
        var settings = await store.GetMethodSettingsAsync(cancellationToken);
        return settings.OrderBy(s => s.DisplayOrder).ThenBy(s => s.SystemName, StringComparer.Ordinal).Select(ViewOf).ToList();
    }

    public async Task<CatalogResult<PaymentMethodView>> UpdateMethodAsync(
        string systemName, bool enabled, int displayOrder, int actorCustomerId, CancellationToken cancellationToken)
    {
        if (displayOrder is < 0 or > 10_000) return CatalogResult.Failure<PaymentMethodView>("displayOrder", "The order must be between 0 and 10000.");

        var setting = (await store.GetMethodSettingsAsync(cancellationToken))
            .FirstOrDefault(s => string.Equals(s.SystemName, systemName, StringComparison.OrdinalIgnoreCase));
        if (setting is null) return CatalogResult.Error<PaymentMethodView>(CatalogErrors.NotFound);

        // A method that has no provider cannot be switched on: customers would be offered something that cannot work.
        if (enabled && !registered.ContainsKey(setting.SystemName))
            return CatalogResult.Error<PaymentMethodView>(PaymentErrors.MethodUnavailable);

        setting.Enabled = enabled;
        setting.DisplayOrder = displayOrder;
        setting.UpdatedOnUtc = clock.UtcNow;
        if (!await store.UpdateMethodSettingAsync(setting, cancellationToken)) return CatalogResult.Error<PaymentMethodView>(CatalogErrors.NotFound);

        await auditLog.WriteAsync("payment.method_updated", actorCustomerId, entityType: "PaymentMethod",
            details: new { method = setting.SystemName, enabled, displayOrder }, cancellationToken: cancellationToken);
        return CatalogResult.Success(ViewOf(setting));
    }

    public Task<PagedResult<PaymentTransaction>> GetPaymentsAsync(PaymentQuery query, CancellationToken cancellationToken) =>
        store.GetPagedAsync(query with { Page = Math.Max(query.Page, 1), PageSize = Math.Clamp(query.PageSize, 1, 100) }, cancellationToken);

    public Task<PaymentTransaction?> GetPaymentAsync(int id, CancellationToken cancellationToken) => store.GetAsync(id, cancellationToken);

    public string? GetRedirectUrl(PaymentTransaction payment) => registered.TryGetValue(payment.Method, out var provider) ? provider.GetRedirectUrl(payment) : null;

    public Task<PaymentTransaction?> FindByProviderReferenceAsync(string method, string providerReference, CancellationToken cancellationToken) =>
        store.GetByProviderReferenceAsync(method, providerReference, cancellationToken);

    public async Task<CatalogResult<PaymentTransaction>> CaptureAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var payment = await store.GetAsync(id, cancellationToken);
        if (payment is null) return CatalogResult.Error<PaymentTransaction>(CatalogErrors.NotFound);
        if (!PaymentRules.CanCapture(payment.Status)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.InvalidState);
        if (!registered.TryGetValue(payment.Method, out var provider)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.MethodUnavailable);

        var answer = await provider.CaptureAsync(payment, cancellationToken);
        if (!answer.Succeeded) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed);

        return await ChangeAsync(payment, PaymentStatus.Paid, payment.RefundedAmount, answer.ProviderReference, "payment.captured", actorCustomerId, cancellationToken);
    }

    public async Task<CatalogResult<PaymentTransaction>> VoidAsync(int id, int actorCustomerId, CancellationToken cancellationToken)
    {
        var payment = await store.GetAsync(id, cancellationToken);
        if (payment is null) return CatalogResult.Error<PaymentTransaction>(CatalogErrors.NotFound);
        if (!PaymentRules.CanVoid(payment.Status)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.InvalidState);
        if (!registered.TryGetValue(payment.Method, out var provider)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.MethodUnavailable);

        var answer = await provider.VoidAsync(payment, cancellationToken);
        if (!answer.Succeeded) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed);

        return await ChangeAsync(payment, PaymentStatus.Voided, payment.RefundedAmount, answer.ProviderReference, "payment.voided", actorCustomerId, cancellationToken);
    }

    public async Task<CatalogResult<PaymentTransaction>> RefundAsync(int id, decimal amount, int actorCustomerId, CancellationToken cancellationToken)
    {
        var payment = await store.GetAsync(id, cancellationToken);
        if (payment is null) return CatalogResult.Error<PaymentTransaction>(CatalogErrors.NotFound);
        if (!PaymentRules.CanRefund(payment.Status)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.InvalidState);

        var currency = await primaryCurrency.GetPrimaryAsync(cancellationToken);
        if (amount <= 0) return CatalogResult.Failure<PaymentTransaction>("amount", "The amount must be above 0.");
        if (!CurrencyRules.HasValidScale(amount, currency.DecimalPlaces))
            return CatalogResult.Failure<PaymentTransaction>("amount", $"The amount can have at most {currency.DecimalPlaces} decimal places.");
        if (amount > PaymentRules.Refundable(payment)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.RefundExceeds);
        if (!registered.TryGetValue(payment.Method, out var provider)) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.MethodUnavailable);

        var answer = await provider.RefundAsync(payment, amount, cancellationToken);
        if (!answer.Succeeded) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed);

        var status = PaymentRules.AfterRefund(payment.Amount, payment.RefundedAmount, amount);
        return await ChangeAsync(payment, status, payment.RefundedAmount + amount, answer.ProviderReference, "payment.refunded", actorCustomerId, cancellationToken,
            new { payment.Id, amount, refunded = payment.RefundedAmount + amount });
    }

    // ---- Gateways ----

    public async Task<CallbackOutcome> HandleCallbackAsync(
        string provider, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (!registered.TryGetValue(provider, out var adapter)) return CallbackOutcome.Rejected;

        // Nothing in the body is trusted until the adapter has verified the signature.
        var callback = adapter.ParseCallback(body, headers);
        if (callback is null) return CallbackOutcome.Rejected;

        var payment = await store.GetByProviderReferenceAsync(adapter.SystemName, callback.ProviderReference, cancellationToken);
        var rule = PaymentRules.ForCallback(callback.Type);

        var outcome = Ignored;
        if (payment is not null && rule is { } wanted && wanted.From(payment.Status)
            && await store.TryChangeAsync(payment.Id, payment.Status, wanted.Target, payment.RefundedAmount,
                payment.ProviderReference, wanted.Target == PaymentStatus.Failed ? "provider_reported" : null, clock.UtcNow, cancellationToken))
        {
            outcome = Applied;
        }

        // The event id is unique per provider: a second delivery of the same event stops here.
        if (!await store.TryAddEventAsync(adapter.SystemName, callback.EventId, callback.Type, payment?.Id, outcome, clock.UtcNow, cancellationToken))
        {
            // A gateway repeats an event when it got no answer. The first delivery may have died after the status changed and before the order
            // side heard of it, so the handlers run again; they are safe to repeat.
            if (payment is not null) await NotifyHandlersAsync(payment.Id, cancellationToken);
            return CallbackOutcome.Replay;
        }

        await auditLog.WriteAsync(outcome == Applied ? "payment.callback_applied" : "payment.callback_ignored", entityType: "PaymentTransaction",
            entityId: payment?.Id, details: new { provider = adapter.SystemName, callback.EventId, callback.Type }, cancellationToken: cancellationToken);
        if (outcome == Applied) await NotifyHandlersAsync(payment!.Id, cancellationToken);
        return outcome == Applied ? CallbackOutcome.Applied : CallbackOutcome.Ignored;
    }

    // ---- Helpers ----

    /// <summary>Tells the handlers what the payment is now, and does what they ask (give the money back).</summary>
    private async Task NotifyHandlersAsync(int paymentId, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(paymentId, cancellationToken);
        if (current is null || current.Status is not (PaymentStatus.Paid or PaymentStatus.Failed or PaymentStatus.Voided)) return;

        foreach (var handler in handlers)
        {
            if (await handler.OnCallbackAppliedAsync(current, cancellationToken) == PaymentFollowUp.Refund && PaymentRules.CanRefund(current.Status))
                await RefundAsync(current.Id, PaymentRules.Refundable(current), 0, cancellationToken);
        }
    }

    private static CatalogResult<PaymentTransaction> Repeat(
        PaymentTransaction existing, string referenceType, CreatePaymentCommand command, string method)
    {
        var same = existing.ReferenceType == referenceType && existing.ReferenceId == command.ReferenceId
            && string.Equals(existing.Method, method, StringComparison.OrdinalIgnoreCase)
            && existing.Amount == command.Amount && existing.CustomerId == command.CustomerId;
        if (!same) return CatalogResult.Error<PaymentTransaction>(PaymentErrors.IdempotencyConflict);
        return existing.Status == PaymentStatus.Failed
            ? CatalogResult.Error<PaymentTransaction>(PaymentErrors.ProviderFailed)
            : CatalogResult.Success(existing);
    }

    private async Task<bool> IsEnabledAsync(string systemName, CancellationToken cancellationToken) =>
        (await store.GetMethodSettingsAsync(cancellationToken)).Any(s => s.Enabled && string.Equals(s.SystemName, systemName, StringComparison.OrdinalIgnoreCase));

    private PaymentMethodView ViewOf(PaymentMethodSetting setting)
    {
        var found = registered.TryGetValue(setting.SystemName, out var provider);
        return new PaymentMethodView(
            setting.SystemName, found ? provider!.DisplayName : setting.SystemName, found ? provider!.Kind : PaymentProviderKind.Offline,
            setting.Enabled, setting.DisplayOrder, found);
    }

    /// <summary>Applies a status change with compare-and-set. A lost race means the payment moved meanwhile, so it is an invalid state now.</summary>
    private async Task<CatalogResult<PaymentTransaction>> ChangeAsync(
        PaymentTransaction payment, PaymentStatus target, decimal refunded, string? providerReference, string auditEvent, int actorCustomerId,
        CancellationToken cancellationToken, object? details = null)
    {
        if (!await store.TryChangeAsync(payment.Id, payment.Status, target, refunded, providerReference ?? payment.ProviderReference, null, clock.UtcNow, cancellationToken))
            return CatalogResult.Error<PaymentTransaction>(PaymentErrors.InvalidState);

        await AuditAsync(auditEvent, actorCustomerId, payment, details ?? new { payment.Id, status = PaymentRules.ToWire(target) }, cancellationToken);
        return CatalogResult.Success((await store.GetAsync(payment.Id, cancellationToken))!);
    }

    private Task AuditAsync(string eventName, int? customerId, PaymentTransaction payment, object details, CancellationToken cancellationToken) =>
        auditLog.WriteAsync(eventName, customerId, entityType: "PaymentTransaction", entityId: payment.Id, details: details, cancellationToken: cancellationToken);
}
