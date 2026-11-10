using Nomori.Marketplace.Core.Payments;

namespace Nomori.Marketplace.Services.Payments;

/// <summary>Pay on delivery: nothing is asked of anybody. The payment stays pending until an administrator records that the cash arrived.</summary>
public sealed class CashOnDeliveryProvider : IPaymentProvider
{
    public const string Name = "cod";

    public string SystemName => Name;
    public string DisplayName => "Pay on delivery";
    public PaymentProviderKind Kind => PaymentProviderKind.Offline;

    public Task<PaymentProviderResult> InitiateAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    public Task<PaymentProviderResult> CaptureAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    public Task<PaymentProviderResult> VoidAsync(PaymentTransaction transaction, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    // The money went back by hand; the platform only records it.
    public Task<PaymentProviderResult> RefundAsync(PaymentTransaction transaction, decimal amount, CancellationToken cancellationToken) =>
        Task.FromResult(PaymentProviderResult.Ok());

    public string? GetRedirectUrl(PaymentTransaction transaction) => null;

    // An offline method has no gateway to call back.
    public PaymentCallback? ParseCallback(string body, IReadOnlyDictionary<string, string> headers) => null;
}
