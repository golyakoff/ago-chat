using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetDownloadOverageBillingModeAsOwner;

/// <summary>
/// `25-84`: the platform-owner-only, per-tenant billing-mode toggle - "not a tenant-facing setting"
/// (`docs/backlog/25-84-*.md`'s own decision, and its own Out of scope: "any tenant-facing self-service
/// control over the auto-bill/manual toggle stays the platform owner's"). The identical single-command,
/// value-carrying shape
/// <see cref="Ago.Chat.Application.UseCases.SetDownloadBlockExemptionAsOwner.SetDownloadBlockExemptionAsOwner"/>
/// established for `25-83`'s own sibling override, deliberately reused rather than reshaped.
/// </summary>
/// <param name="Reason">Required, non-blank, in both directions - moving a tenant onto auto-bill starts
/// charging them without further consent, and moving them off it can block them; neither is a change
/// anybody should be able to make without saying why.</param>
public sealed record SetDownloadOverageBillingModeAsOwner(
    SiteId SiteId, DownloadOverageBillingMode Mode, string SetBy, string Reason);
