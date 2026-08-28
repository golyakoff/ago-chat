namespace Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;

/// <summary>
/// `13-02`: the already-verified, already-parsed shape `POST /api/v1/billing/webhooks/yookassa`'s own
/// endpoint hands this handler - signature verification and ЮKassa JSON parsing both happen in
/// `Ago.Chat.Api`/`Ago.Chat.Infrastructure.YooKassa` before this command is ever constructed (the same
/// "endpoint verifies auth and parses the provider payload, Application handler takes clean neutral
/// values" split `MaxWebhookEndpoints`/`ReceiveChannelMessage` already establish for MAX's own inbound
/// webhook). Deliberately carries no <c>SiteId</c> - see `TenantScopeExemptions`'s own entry for
/// <c>ProcessYooKassaWebhookHandler.HandleAsync</c> for why that is safe here.
/// </summary>
public sealed record ProcessYooKassaWebhook(string YooKassaPaymentId, string EventType, string? PaymentMethodId);
