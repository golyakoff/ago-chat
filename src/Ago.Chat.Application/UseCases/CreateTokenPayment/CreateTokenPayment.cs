using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateTokenPayment;

/// <summary>`26-291`: `POST /api/v1/sites/{siteId}/billing/checkout-sessions/token`'s own command - the
/// YooKassa Android SDK's own tokenization flow (`docs/backlog/26-289-yookassa-android-sdk.md`) handing
/// this codebase a one-time, single-use <paramref name="PaymentToken"/> in place of
/// <see cref="Application.UseCases.CreateCheckoutSession.CreateCheckoutSession"/>'s browser redirect. Same
/// <see cref="Domain.Permission.SiteConfigure"/> gate, same seat-count shape - this is the identical use
/// case (create a pending payment, save the subscription) reaching the same port a second way, not a
/// different one.</summary>
public sealed record CreateTokenPayment(OperatorId RequestedBy, SiteId SiteId, int RequestedSeats, string PaymentToken);

/// <summary>`26-291`: <paramref name="Status"/> is ЮKassa's own payment status, carried through
/// unmapped so the app can decide whether to run its own `createConfirmationIntent` (when
/// <paramref name="ConfirmationUrl"/> is present, meaning a 3DS/SberPay step is needed) or simply start
/// polling `GET .../billing/status` for the webhook-driven grant (when it is not). Neither field is
/// itself proof of payment - only <c>ProcessYooKassaWebhookHandler</c>'s own re-query ever grants
/// anything (`adr/0190`, unchanged by this item).</summary>
public sealed record CreateTokenPaymentResult(string Status, string? ConfirmationUrl);
