namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-02`: the outbound half of this item's ЮKassa integration - creates a checkout-session payment
/// (`confirmation.type = redirect`, `save_payment_method = true`) and hands back the redirect URL a
/// caller sends the operator's browser to. Deliberately provider-neutral in every member name and
/// type, the same discipline `ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure`
/// enforces for `IInboundChannelAdapter` - <c>Ago.Chat.Infrastructure.YooKassa</c> is the only project
/// that may know this is ЮKassa specifically, what its request/response JSON shapes are, or how its
/// auth (Basic, shop id + secret key) works.
/// </summary>
public interface IYooKassaPaymentsClient
{
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>`13-03`: the recurring-charge half - a "charge on file" payment against a
    /// <see cref="BillingSubscription.PaymentMethodId"/> a prior <see cref="CreatePaymentAsync"/> call
    /// already saved (`save_payment_method = true`), with no `confirmation` object and nobody's browser
    /// involved: ЮKassa's own documented shape for a merchant-initiated recurring payment. Used by both
    /// the recurring-charge job (an on-time renewal, a `PastDue` retry) and a mid-cycle upgrade's own
    /// immediate prorated charge - every caller in this codebase that charges a seat count already on
    /// file, rather than starting a fresh checkout.</summary>
    Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
        ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken);
}

/// <summary><paramref name="IdempotenceKey"/> is this call's own retry-safety, not the webhook ledger's
/// (<c>BillingWebhookEvent</c>) - ЮKassa's own Payments API requires an `Idempotence-Key` header on
/// every payment-creation call so a client's own network retry of this exact request cannot create two
/// payments for one checkout attempt.</summary>
public sealed record CreatePaymentRequest(decimal AmountRub, string Description, string ReturnUrl, string IdempotenceKey);

/// <summary>
/// The terminal/transient split <c>TelegramApiClient</c>/<c>MaxApiClient</c> already established for
/// this codebase's other outbound third-party clients: a response the provider actually answered but
/// refused (a malformed request, bad credentials, an unprocessable amount) comes back as a value;
/// anything shaped like "the provider or the network failed" (5xx, an unreachable host, a timeout)
/// throws, so the caller's own resilience/retry story is not this class's job to reimplement.
/// </summary>
public abstract record CreatePaymentResult
{
    private CreatePaymentResult()
    {
    }

    public sealed record Success(string PaymentId, string ConfirmationUrl) : CreatePaymentResult;

    public sealed record Refused(string Reason) : CreatePaymentResult;
}

/// <summary>`13-03`: <paramref name="IdempotenceKey"/> carries the same job here as
/// <see cref="CreatePaymentRequest.IdempotenceKey"/> does for a checkout, with an extra duty: this
/// codebase's own recurring-charge job derives it deterministically from
/// <c>(BillingSubscriptionId, attempt date)</c> rather than a fresh id per call
/// (`SubscriptionRenewalApplier`'s own remarks) - the reason is not "the network might retry this exact
/// HTTP request" (true here too), but "two `Ago.Chat.Worker` replicas might both decide the same
/// subscription is due on the same day". A deterministic key turns that race into ЮKassa itself
/// returning the one real payment's result twice, rather than two real charges.</summary>
public sealed record ChargeStoredPaymentMethodRequest(decimal AmountRub, string Description, string PaymentMethodId, string IdempotenceKey);

/// <summary>The identical terminal/transient split <see cref="CreatePaymentResult"/> already
/// establishes, for the charge-on-file shape.</summary>
public abstract record ChargeStoredPaymentMethodResult
{
    private ChargeStoredPaymentMethodResult()
    {
    }

    public sealed record Success(string PaymentId) : ChargeStoredPaymentMethodResult;

    public sealed record Refused(string Reason) : ChargeStoredPaymentMethodResult;
}
