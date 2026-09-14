using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetDownloadBlockExemptionAsOwner;

/// <summary>
/// `25-83`: the platform owner's own free, indefinite bypass of a tenant's hard download-block
/// threshold - "an owner-controlled per-tenant override: a platform-owner-only toggle, off by
/// default... a manual exception, not a product feature a tenant can reach themselves"
/// (`docs/backlog/25-83-*.md`'s own decision). The identical shape
/// <see cref="Ago.Chat.Application.UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwner"/>
/// already establishes for a different owner-only all-or-nothing flag: one command for both
/// directions (<paramref name="Exempt"/> carries which), never two separate grant/revoke commands.
/// </summary>
/// <param name="Reason">Required, non-blank, on every change - both directions are equally
/// consequential (`SetDownloadBlockExemptionAsOwnerHandler`'s own remarks).</param>
public sealed record SetDownloadBlockExemptionAsOwner(SiteId SiteId, bool Exempt, string SetBy, string Reason);
