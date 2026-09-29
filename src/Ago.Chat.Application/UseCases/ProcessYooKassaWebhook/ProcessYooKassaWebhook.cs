namespace Ago.Chat.Application.UseCases.ProcessYooKassaWebhook;

/// <summary>
/// `26-286`: carries only the payment id the notification named - and even that only as a lookup key,
/// never as a fact. ЮKassa does not sign its console-configured HTTP notifications, so the notification
/// body is untrusted input in full: the handler re-queries the payment object from ЮKassa's own API by
/// this id and drives every decision (succeeded/canceled, the saved payment-method id) from that
/// authoritative reply, never from anything the notification claimed. That is why this command no longer
/// carries the event type or payment-method id it used to - both were fields the sender could set to
/// anything, and both now come from the re-query instead (`adr/0071` superseded by `adr/0190`).
///
/// <para>Deliberately carries no <c>SiteId</c> - see `TenantScopeExemptions`'s own entry for
/// <c>ProcessYooKassaWebhookHandler.HandleAsync</c> for why that is safe here.</para>
/// </summary>
public sealed record ProcessYooKassaWebhook(string YooKassaPaymentId);
