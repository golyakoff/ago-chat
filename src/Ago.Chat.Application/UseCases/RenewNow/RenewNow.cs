using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RenewNow;

/// <summary>
/// `26-296`: `POST /api/v1/sites/{siteId}/billing/subscriptions/{id}/renew-now`'s own command - an
/// operator paying ahead of <see cref="BillingSubscription.CurrentPeriodEnd"/> rather than waiting for
/// `Ago.Chat.Worker`'s own automatic sweep (`ProcessSubscriptionRenewalHandler`). Same
/// <see cref="Domain.Permission.SiteConfigure"/> gate every other billing write in this file uses.
///
/// <para><b>Base subscriptions only, by design</b>
/// (`docs/backlog/26-290-console-billing-redesign.md`'s own "simplest first cut is base-only, with
/// option rows left to their own schedule") - a channel add-on's own <see cref="BillingSubscription"/>
/// row keeps renewing on its own aligned period end, untouched by this command.</para>
/// </summary>
public sealed record RenewNow(OperatorId RequestedBy, SiteId SiteId, BillingSubscriptionId SubscriptionId);

/// <param name="AmountRub">The recurring amount actually charged - seats plus purchased extra
/// Administrators, at the currently-effective prices, the identical components
/// <c>GetBillingStatusHandler</c>'s own <c>nextChargeRub</c> already estimates in advance
/// (`26-295`).  Deliberately excludes download overage - variable, only known at an ordinary renewal's
/// own moment, never invented ahead of it (the identical reasoning `nextChargeRub`'s own remarks
/// give).</param>
/// <param name="NewPeriodEnd">The subscription's own new <see cref="BillingSubscription.CurrentPeriodEnd"/>
/// after this charge - the prior value plus one <see cref="BillingSubscription.PeriodLength"/>, the
/// identical "extend from the period end, not from now" rule every ordinary renewal already
/// follows.</param>
public sealed record RenewNowResult(decimal AmountRub, DateTimeOffset NewPeriodEnd);
