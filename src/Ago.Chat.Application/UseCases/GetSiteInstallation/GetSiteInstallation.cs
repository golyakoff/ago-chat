using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetSiteInstallation;

/// <summary>
/// `10-06`: operator-authenticated, `site:configure`-gated - the identical shape
/// <see cref="GetWidgetConfig.GetWidgetConfig"/> already uses for a site-scoped admin read, reused
/// rather than a new permission. The blast radius is the same: a site's own current configuration,
/// read back by an operator of that site. <see cref="Domain.Site.PublicKey"/> is not a secret
/// (`api-design.md`, `adr/0029`) - a visitor's browser is handed it on every widget bootstrap - but it
/// still identifies which tenant a visitor session belongs to, so it is returned to that site's own
/// operators and nobody else, the same "not a secret, but not unguarded" posture `Ago.Chat.Domain.Site`
/// already states in its own doc comment.
/// </summary>
public sealed record GetSiteInstallation(SiteId SiteId, OperatorId RequestedBy);
