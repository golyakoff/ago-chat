using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.PurchaseDownloadOverage;

/// <summary>
/// `25-84`: the <see cref="DownloadOverageBillingMode.Manual"/> path's own entry point -
/// `POST /api/v1/sites/{siteId}/download-overage/checkout-sessions`, the tenant choosing to pay for the
/// egress they have already used so a blocked account starts reading files again. Carries no amount:
/// "however many gigabytes over, right now" is computed server-side at the instant of the call, which
/// is the whole difference between this and every fixed-price purchase in this codebase.
///
/// <para>Gated by <see cref="Permission.SiteConfigure"/> - the identical permission
/// <c>CreateCheckoutSessionHandler</c> and every other billing write already require, for the identical
/// reason `13-02`'s own Scope gives: committing the account to a charge is a site-configuration act,
/// not a conversation one.</para>
/// </summary>
public sealed record PurchaseDownloadOverage(OperatorId RequestedBy, SiteId SiteId);

/// <param name="ConfirmationUrl">ЮKassa's own hosted checkout page - the caller redirects the
/// operator's browser here. Returning from it proves nothing; only the webhook does
/// (<c>BillingWebhookApplier</c>, and `13-02`'s own "never the redirect alone").</param>
/// <param name="AmountRub">What was actually asked for, computed from <paramref name="BytesOver"/> at
/// the currently-published per-gigabyte price - reported back so the console can show the figure it is
/// sending someone to pay rather than recomputing one of its own.</param>
/// <param name="BytesOver">The unsettled bytes past this tenant's own hard threshold that
/// <paramref name="AmountRub"/> covers.</param>
public sealed record DownloadOverageCheckoutDto(string ConfirmationUrl, decimal AmountRub, long BytesOver);
