namespace Ago.Chat.Domain;

/// <summary>
/// `23-85`/`adr/0151`: "one entitlement per channel kind, not one class-wide" - the author's own
/// decision, 2026-09-09 (the backlog item's own "Three questions, answered" section): the per-channel
/// reading of the price list ("any one of the available channels, +100 ₽" - `ago-business` `0012`'s own
/// price table) is the decision, not merely the likely commercial intent, and the schema carries a
/// separate entitlement per <see cref="ChannelKind"/> rather than one that covers the whole class.
///
/// <para><b>Pure Domain, not deployment configuration.</b> Unlike
/// <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/>'s own
/// <see cref="BillingOptionKey"/> -&gt; <see cref="ModuleKey"/> mapping - genuinely opaque to this
/// assembly, resolved only by whatever the deployment declares - the question this type answers
/// ("which billing option corresponds to this channel kind") is not opaque at all:
/// <see cref="ChannelKind"/> is this assembly's own enum, already known here, and the naming
/// convention below (<c>"channel-" + the enum member's own lowercase name</c>) is a stable fact about
/// this codebase's own vocabulary, not something a deployment could reasonably want to override
/// per-environment the way an entry point URL or a permission list legitimately can. Making it
/// deployment configuration instead would just be a second place this same fact could drift from
/// itself, for no configurability anyone has ever asked for.</para>
///
/// <para><b>Still resolved through <see cref="Application.Abstractions.IBillingOptionEntitlementProvider"/>
/// afterward, never used to look up a <see cref="ModuleKey"/> directly.</b> This type only says which
/// <see cref="BillingOptionKey"/> a channel kind corresponds to in the price list's own vocabulary -
/// exactly the string a deployment's <c>BillingOptionEntitlements__channel-*</c> configuration line is
/// keyed by. Whether that option is actually mapped to a <see cref="ModuleKey"/> at all, and to which
/// one, stays the deployment's call, made through that port - the identical split
/// <see cref="BillingOptionKey"/>'s own remarks draw between "the commercial catalog's key" and "what
/// buying it turns on."</para>
/// </summary>
public static class ChannelEntitlementOptionKeys
{
    /// <summary>The billing option key this channel kind's own entitlement is named by - always
    /// <c>"channel-" + kind.ToString().ToLowerInvariant()</c>, e.g. <see cref="ChannelKind.Telegram"/>
    /// -&gt; <c>"channel-telegram"</c>, <see cref="ChannelKind.WhatsApp"/> -&gt; <c>"channel-whatsapp"</c>.
    /// A plain <see langword="switch"/> over the enum, deliberately, rather than deriving the string at
    /// runtime: an explicit arm per <see cref="ChannelKind"/> member fails to compile the moment a new
    /// one is added without a matching price-list key having been considered, where a computed string
    /// would silently mint a plausible-looking key for a channel nobody has actually priced yet.
    /// </summary>
    public static BillingOptionKey For(ChannelKind kind) => kind switch
    {
        ChannelKind.Max => new BillingOptionKey("channel-max"),
        ChannelKind.Sms => new BillingOptionKey("channel-sms"),
        ChannelKind.Telegram => new BillingOptionKey("channel-telegram"),
        ChannelKind.WhatsApp => new BillingOptionKey("channel-whatsapp"),
        ChannelKind.Vk => new BillingOptionKey("channel-vk"),
        ChannelKind.Avito => new BillingOptionKey("channel-avito"),
        ChannelKind.Email => new BillingOptionKey("channel-email"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, $"No billing option key is defined for channel kind '{kind}'."),
    };
}
