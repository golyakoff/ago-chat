using System.Text.Json.Serialization;

namespace Ago.Chat.Infrastructure.YooKassa;

/// <summary>
/// `13-02`: ЮKassa's own wire shapes - kept in this project alone
/// (`ChannelPortTests.NoProviderVocabulary_AppearsAboveInfrastructure`'s own discipline for a channel
/// provider, extended here to a payment provider for the same reason: nothing above the Infrastructure
/// boundary may know ЮKassa's own JSON field names). <b>Not confirmed against ЮKassa's real API</b> -
/// this environment has no live Shop ID/Secret Key/Webhook Key and no network access to ЮKassa's own
/// documentation host, so every shape below is built from this item's own backlog text plus the
/// well-known, publicly documented ЮKassa Payments API contract as of this item's own knowledge cutoff.
/// The field names, the terminal/transient status-code split, and the webhook envelope's own shape are
/// the parts most likely to need a real-credential correction - see this item's own report for exactly
/// which claims are asserted here versus verified against a real ЮKassa test-mode call.
/// </summary>
public sealed record YooKassaCreatePaymentRequest(
    [property: JsonPropertyName("amount")] YooKassaAmount Amount,
    [property: JsonPropertyName("capture")] bool Capture,
    [property: JsonPropertyName("confirmation")] YooKassaConfirmationRequest Confirmation,
    [property: JsonPropertyName("save_payment_method")] bool SavePaymentMethod,
    [property: JsonPropertyName("description")] string Description);

/// <summary>`26-291`: the SDK tokenization shape - `payment_token` in place of `confirmation`/
/// `save_payment_method`, ЮKassa's own documented request shape for redeeming a mobile-SDK token.
/// Deliberately no `save_payment_method` here: the SDK flow does not (yet) offer to store the method for
/// a future recurring charge the way the redirect flow's `CreatePaymentAsync` does - `26-291`'s own Scope
/// is the first token payment only, not recurring billing on a token-originated card.</summary>
public sealed record YooKassaCreatePaymentWithTokenRequest(
    [property: JsonPropertyName("amount")] YooKassaAmount Amount,
    [property: JsonPropertyName("capture")] bool Capture,
    [property: JsonPropertyName("payment_token")] string PaymentToken,
    [property: JsonPropertyName("description")] string Description);

/// <summary>`13-03`: the charge-on-file shape - no `confirmation` object (nobody's browser is involved)
/// and `payment_method_id` in place of `save_payment_method`, ЮKassa's own documented request shape for
/// a merchant-initiated recurring payment against a previously saved method.</summary>
public sealed record YooKassaChargeStoredPaymentMethodRequest(
    [property: JsonPropertyName("amount")] YooKassaAmount Amount,
    [property: JsonPropertyName("capture")] bool Capture,
    [property: JsonPropertyName("payment_method_id")] string PaymentMethodId,
    [property: JsonPropertyName("description")] string Description);

public sealed record YooKassaAmount(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("currency")] string Currency);

public sealed record YooKassaConfirmationRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("return_url")] string ReturnUrl);

/// <summary><see cref="Paid"/> and <see cref="PaymentMethod"/> are populated on a `26-286` re-query
/// (<c>GET /payments/{id}</c>) - the create-payment reply this same shape also models leaves them at
/// their defaults, which is harmless because that path only ever reads <see cref="Id"/> and
/// <see cref="Confirmation"/>. On a re-query these two are the authoritative facts the webhook path acts
/// on: <see cref="Status"/>=`succeeded`/`Paid`=true is the only combination that grants, and
/// <see cref="PaymentMethod"/>.<see cref="YooKassaPaymentMethod.Id"/> is the saved card id stored on the
/// subscription - both read from ЮKassa's own reply here, never from the notification body.</summary>
public sealed record YooKassaPaymentResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("paid")] bool Paid,
    [property: JsonPropertyName("confirmation")] YooKassaConfirmationResponse? Confirmation,
    [property: JsonPropertyName("payment_method")] YooKassaPaymentMethod? PaymentMethod);

public sealed record YooKassaConfirmationResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("confirmation_url")] string? ConfirmationUrl);

/// <summary>ЮKassa's own documented error envelope, returned with a client-shaped (400/401/403/404)
/// status - <see cref="Description"/> is the human-readable half this item surfaces as
/// <c>CreatePaymentResult.Refused.Reason</c>.</summary>
public sealed record YooKassaErrorResponse(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("description")] string? Description);

/// <summary>The webhook notification envelope - <see cref="Event"/> is ЮKassa's own event name
/// (`payment.succeeded`, `payment.waiting_for_capture`, `payment.canceled`, ...),
/// <see cref="YooKassaObject.Id"/> is the payment id this item's own idempotency ledger keys on.</summary>
public sealed record YooKassaWebhookEnvelope(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("object")] YooKassaObject Object);

public sealed record YooKassaObject(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payment_method")] YooKassaPaymentMethod? PaymentMethod);

/// <summary>Present only once a card has actually been saved (<c>save_payment_method = true</c> on the
/// original request, and the payment succeeded) - <see cref="Id"/> is what `13-03`'s future renewal
/// charge would present back to ЮKassa; this item only ever stores it
/// (<c>BillingSubscription.MarkSucceeded</c>).</summary>
public sealed record YooKassaPaymentMethod([property: JsonPropertyName("id")] string Id);
