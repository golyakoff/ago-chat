using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateCheckoutSession;

/// <summary>`13-02`: `POST /api/v1/sites/{siteId}/billing/checkout-sessions`'s own command - an
/// operator choosing a seat count to subscribe at. Carries <see cref="SiteId"/>, gated by
/// <see cref="Domain.Permission.SiteConfigure"/> (`TenantScopeTests`'s own RBAC-gated shape) - a
/// billing/tier change is a site-configuration action, the same permission `5-08` already granted
/// `"Admin"` for exactly this kind of decision (this item's own Scope note).
///
/// <para><b>`26-299`: <see cref="SavePaymentMethod"/> - the operator's own choice, not always
/// <see langword="true"/>.</b> Every instant, charge-now write this codebase has (a mid-cycle upgrade, an
/// Administrator/channel purchase, pay-early) needs a stored card to work at all; declining to save one
/// trades that convenience for never handing ЮKassa a reusable card token - a real, informed choice the
/// console's billing-v2 screen surfaces as a checkbox, not a decision this codebase may keep making for
/// the operator by always passing `true`.</para></summary>
public sealed record CreateCheckoutSession(OperatorId RequestedBy, SiteId SiteId, int RequestedSeats, bool SavePaymentMethod);

public sealed record CheckoutSessionDto(string ConfirmationUrl);
