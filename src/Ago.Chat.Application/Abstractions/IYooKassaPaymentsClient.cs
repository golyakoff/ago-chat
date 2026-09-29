namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `13-02`: the outbound half of this item's ЮKassa integration - creates a checkout-session payment
/// (`confirmation.type = redirect`) and hands back the redirect URL a caller sends the operator's
/// browser to. `26-299`: `save_payment_method` is the caller's own choice
/// (<see cref="CreatePaymentRequest.SavePaymentMethod"/>), no longer always `true`. Deliberately
/// provider-neutral in every member name and
/// type, the same discipline `ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure`
/// enforces for `IInboundChannelAdapter` - <c>Ago.Chat.Infrastructure.YooKassa</c> is the only project
/// that may know this is ЮKassa specifically, what its request/response JSON shapes are, or how its
/// auth (Basic, shop id + secret key) works.
/// </summary>
public interface IYooKassaPaymentsClient
{
    Task<CreatePaymentResult> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    /// <summary>`26-291`: the YooKassa Android SDK's own tokenization half
    /// (`docs/backlog/26-289-yookassa-android-sdk.md`) - the app collects card/SBP/SberPay details
    /// entirely inside the SDK's own UI and exchanges them for a one-time, single-use `payment_token`;
    /// this call is what redeems that token into a real payment, `POST /payments` with `payment_token`
    /// in place of <see cref="CreatePaymentRequest"/>'s `confirmation` object. Same ShopId/SecretKey
    /// Basic auth and `Idempotence-Key` discipline as <see cref="CreatePaymentAsync"/> - the two differ
    /// only in which field tells ЮKassa how the buyer is paying, never in who is allowed to call
    /// `POST /payments` or how. Unlike the redirect flow, the SDK's own confirmation step (3DS/SberPay)
    /// runs inside the app, not a browser this codebase redirects - so this call's own result carries a
    /// `Status` and an optional `ConfirmationUrl` for the app to hand to its own confirmation intent,
    /// rather than assuming a browser confirmation always exists.</summary>
    Task<CreatePaymentWithTokenResult> CreatePaymentWithTokenAsync(
        CreatePaymentWithTokenRequest request, CancellationToken cancellationToken);

    /// <summary>`13-03`: the recurring-charge half - a "charge on file" payment against a
    /// <see cref="BillingSubscription.PaymentMethodId"/> a prior <see cref="CreatePaymentAsync"/> call
    /// already saved (`save_payment_method = true`), with no `confirmation` object and nobody's browser
    /// involved: ЮKassa's own documented shape for a merchant-initiated recurring payment. Used by both
    /// the recurring-charge job (an on-time renewal, a `PastDue` retry) and a mid-cycle upgrade's own
    /// immediate prorated charge - every caller in this codebase that charges a seat count already on
    /// file, rather than starting a fresh checkout.</summary>
    Task<ChargeStoredPaymentMethodResult> ChargeStoredPaymentMethodAsync(
        ChargeStoredPaymentMethodRequest request, CancellationToken cancellationToken);

    /// <summary>`26-286`: re-query a payment by its id and read back its <b>authoritative</b> state -
    /// ЮKassa's own documented way to verify a console-configured HTTP notification (there is no HMAC
    /// signature or shared "webhook key" on those, `adr/0071` was wrong on that point (superseded by
    /// `adr/0190`) - the real
    /// mechanism is "never trust the notification body's status; fetch the payment object and act on
    /// what the API says"). The one call the webhook path makes before applying anything: it is what
    /// turns a forged or replayed notification into a no-op, because a forger cannot make ЮKassa's own
    /// Payments API report a payment as `succeeded` that is not.</summary>
    Task<GetPaymentResult> GetPaymentAsync(string paymentId, CancellationToken cancellationToken);
}

/// <summary><paramref name="IdempotenceKey"/> is this call's own retry-safety, not the webhook ledger's
/// (<c>BillingWebhookEvent</c>) - ЮKassa's own Payments API requires an `Idempotence-Key` header on
/// every payment-creation call so a client's own network retry of this exact request cannot create two
/// payments for one checkout attempt.
///
/// <para><b>`26-299`: <see cref="SavePaymentMethod"/> - the operator's own checkout-time choice, no
/// longer hardcoded <see langword="true"/>.</b> See <c>CreateCheckoutSession</c>'s own remarks for why
/// this must be a real choice rather than one this codebase keeps making on the operator's behalf.</para>
/// </summary>
public sealed record CreatePaymentRequest(decimal AmountRub, string Description, string ReturnUrl, bool SavePaymentMethod, string IdempotenceKey);

/// <summary>`26-291`: <see cref="CreatePaymentRequest"/>'s own token-payment sibling - carries a
/// <paramref name="PaymentToken"/> (the SDK's one-time, single-use tokenization result, opaque here by
/// design: this port never inspects or validates its shape, only forwards it) in place of a return URL,
/// since a token payment's own confirmation step - when ЮKassa's reply says one is needed - happens
/// inside the app's own SDK confirmation intent, never a browser redirect.
///
/// <para><b>`26-299`: <see cref="SavePaymentMethod"/> joins this shape too.</b> Before this item, the
/// SDK tokenization flow deliberately carried no such field ("the SDK flow does not (yet) offer to store
/// the method for a future recurring charge" - this type's own prior remarks, `26-291`'s stated scope).
/// That was a product-scope decision, not an API limitation: ЮKassa's Payments API documents
/// `save_payment_method` as an ordinary top-level `POST /payments` field regardless of whether
/// `confirmation` or `payment_token` supplies the buyer's own payment details, the identical field
/// <see cref="CreatePaymentRequest"/> already threads. Widened here so the operator's own choice applies
/// to both checkout flows, not only the redirect one - not confirmed against a live credential, the same
/// caveat every shape in `YooKassaDtos` already carries.</para></summary>
public sealed record CreatePaymentWithTokenRequest(decimal AmountRub, string Description, string PaymentToken, bool SavePaymentMethod, string IdempotenceKey);

/// <summary>`26-291`: the identical terminal/transient split <see cref="CreatePaymentResult"/> already
/// establishes, reshaped for the token flow's own two differences: <see cref="Success.Status"/> is
/// carried explicitly (a token payment can come back already `succeeded`, not only `pending`), and
/// <see cref="Success.ConfirmationUrl"/> is honestly nullable - present only when ЮKassa's own reply
/// says a further confirmation step (3DS/SberPay) is needed, absent when the charge captured outright.
/// The webhook re-query (`adr/0190`) remains the only thing that ever grants an entitlement - this
/// result, like <see cref="CreatePaymentResult"/>'s, is never treated as proof of payment on its
/// own.</summary>
public abstract record CreatePaymentWithTokenResult
{
    private CreatePaymentWithTokenResult()
    {
    }

    public sealed record Success(string PaymentId, string Status, string? ConfirmationUrl) : CreatePaymentWithTokenResult;

    public sealed record Refused(string Reason) : CreatePaymentWithTokenResult;
}

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

/// <summary>`26-286`: the authoritative payment object <see cref="IYooKassaPaymentsClient.GetPaymentAsync"/>
/// reads back. <see cref="NotFound"/> is the answer for a payment id ЮKassa has no record of - a forged
/// or mistyped notification - and is a legitimate, expected outcome the webhook path acks `200` for, not
/// an error. Everything the caller acts on (<see cref="Found.Status"/>, <see cref="Found.Paid"/>,
/// <see cref="Found.PaymentMethodId"/>) comes from ЮKassa's own reply here, never from the notification
/// body. Only <see cref="NotFound"/> and a successful read are values; a transient/misconfigured response
/// (401/403/429/5xx, a network fault) throws, the same terminal/transient split
/// <see cref="CreatePaymentResult"/> already establishes - a re-query that could not complete must never
/// be mistaken for "payment does not exist" and silently drop a real grant.</summary>
public abstract record GetPaymentResult
{
    private GetPaymentResult()
    {
    }

    public sealed record Found(string PaymentId, string Status, bool Paid, string? PaymentMethodId) : GetPaymentResult;

    public sealed record NotFound : GetPaymentResult;
}
