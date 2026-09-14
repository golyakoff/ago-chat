using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases;

/// <summary>
/// `23-85`/`adr/0151`: "may this account use this channel kind at all" - the one check
/// <c>RegisterChannelCredentialHandler</c>, <c>RevokeChannelCredentialHandler</c> and
/// <c>GetChannelCredentialStatusHandler</c> each need and none of them owned before this item (all
/// three checked only <see cref="Permission.ChannelManage"/>, which answers a different question -
/// see <see cref="ConversationErrors.ChannelNotEntitled"/>'s own remarks).
///
/// <para><b>A static helper composing two existing ports, not a new port of its own.</b> CLAUDE.md
/// rule 2 requires a port for every <em>external resource</em> - a database, an HTTP call, a clock.
/// This method touches none: it calls <see cref="IBillingOptionEntitlementProvider.TryGet"/> and
/// <see cref="IModuleQuantityGrantStore.GetQuantityAsync"/>, two ports each handler already receives
/// (or gains) by constructor injection, and does nothing a unit test cannot already reach through the
/// fakes those two ports already have. A third port here would let a caller mock "is this channel
/// entitled" directly, hiding the actual two-step resolution (<see cref="ChannelEntitlementOptionKeys"/>
/// then the quantity read) a reviewer needs to see to trust the per-channel-kind decision this item
/// makes - the identical "no port for pure orchestration of existing ports" judgement
/// <c>ChannelCredentialTokenValidator</c>'s own placement (a static class, not a port) already applies
/// to a different kind of shared, dependency-free logic.</para>
/// </summary>
public static class ChannelEntitlement
{
    /// <summary>
    /// `23-86`'s own "effective, not raw" quantity - a lapsed billing-driven grant with the platform
    /// owner's unconditional flag still set reads as entitled here for the identical reason
    /// <see cref="IModuleQuantityGrantStore.GetQuantityAsync"/>'s own remarks give: nothing in this
    /// codebase has ever wanted the pre-flag number instead of what is effectively granted right now.
    /// </summary>
    public static async Task<bool> IsEntitledAsync(
        IBillingOptionEntitlementProvider entitlements, IModuleQuantityGrantStore grants, SiteId siteId,
        ChannelKind kind, CancellationToken cancellationToken)
    {
        var optionKey = ChannelEntitlementOptionKeys.For(kind);
        var moduleKey = entitlements.TryGet(optionKey);
        if (moduleKey is null)
        {
            // `IBillingOptionEntitlementProvider`'s own remarks: "null is a legible refusal, never a
            // hidden default." A deployment that has not declared what channel-<kind> turns on has
            // declared, in effect, that nothing may use it yet - the same deny-by-default posture as
            // an actual zero quantity, not a bypass of the gate this item exists to add.
            return false;
        }

        var quantity = await grants.GetQuantityAsync(siteId, moduleKey.Value, cancellationToken);
        return quantity > 0;
    }

    /// <summary>The refusal's own wording - "the difference between a support ticket and a purchase"
    /// (`23-85`'s own Scope) - naming the channel kind so a tenant with more than one connected channel
    /// knows exactly which one lapsed.</summary>
    public static Error Refusal(ChannelKind kind) =>
        ConversationErrors.ChannelNotEntitled(
            $"This account has no channel entitlement for {kind} channels. Connect it once the {kind} channel option is purchased.");
}
