using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetUnconditionalModuleGrantAsOwner;

/// <summary>
/// `23-86`/`adr/0159`: the platform owner's own write for case 1 of this item's "Answered, 2026-09-13"
/// section - "every entitlement gets its own unconditional-grant flag ... the flag can only ever be set
/// by the platform owner". A wholly separate command and handler from
/// <see cref="GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwner"/>, not a bool parameter added to
/// it - the identical reasoning <see cref="GrantModuleQuantityAsOwner.GrantModuleQuantityAsOwner"/>'s
/// own remarks give for being separate from its tenant-facing sibling: this write changes what
/// <see cref="Domain.ModuleQuantityGrant.EffectiveQuantity"/> means for this row (an independent,
/// second input), not what <see cref="Domain.ModuleQuantityGrant.Quantity"/> itself is, so collapsing
/// the two into one command would make one field name two different acts depending on which other
/// argument happened to be present.
/// </summary>
/// <param name="Reason">Required, non-blank, whenever this command runs - `adr/0118`'s own "a blank
/// reason is the same failure as a defaulted expiry", mirrored here for the grant side of the identical
/// relationship rather than reinvented. Checked in
/// <see cref="SetUnconditionalModuleGrantAsOwnerHandler"/> before anything else it does, unconditional
/// on <see cref="UnconditionallyGranted"/> - lifting the flag is exactly as consequential an act as
/// setting it (this item's own text: "the entitlement goes off" is a real write, not a formality), so
/// both directions require the identical justification.</param>
/// <param name="ExpiresAt">`25-115`: <see langword="null"/> means indefinite - "бессрочно", the
/// author's own request for the owner's channel-entitlement table this command's first real multi-kind
/// caller adds. Threaded straight through to <see cref="Domain.ModuleQuantityGrant.SetUnconditionalGrant"/>,
/// which see for why it is stamped on the row even when <see cref="UnconditionallyGranted"/> is
/// <see langword="false"/>.</param>
public sealed record SetUnconditionalModuleGrantAsOwner(
    SiteId SiteId, string ModuleKey, bool UnconditionallyGranted, string SetBy, string Reason,
    DateTimeOffset? ExpiresAt = null);
