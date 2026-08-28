using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.CreateCheckoutSession;

/// <summary>`13-02`: `POST /api/v1/sites/{siteId}/billing/checkout-sessions`'s own command - an
/// operator choosing a seat count to subscribe at. Carries <see cref="SiteId"/>, gated by
/// <see cref="Domain.Permission.SiteConfigure"/> (`TenantScopeTests`'s own RBAC-gated shape) - a
/// billing/tier change is a site-configuration action, the same permission `5-08` already granted
/// `"Admin"` for exactly this kind of decision (this item's own Scope note).</summary>
public sealed record CreateCheckoutSession(OperatorId RequestedBy, SiteId SiteId, int RequestedSeats);

public sealed record CheckoutSessionDto(string ConfirmationUrl);
