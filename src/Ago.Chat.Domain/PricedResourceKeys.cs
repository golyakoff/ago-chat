namespace Ago.Chat.Domain;

/// <summary>
/// `25-43`'s own first decision, made mechanical: "code registers the resource's own key... the owner
/// only ever sets or changes the Rouble figure for a key that already exists." This is the registry -
/// the closed, code-defined list of every <see cref="PriceKey"/> a developer has ever wired a real
/// charge site to read. A developer adds one line here the same moment they build whatever feature it
/// prices; the platform owner's own publish surface (<c>PublishPriceVersionHandler</c>) checks a
/// caller-supplied key against this list before ever minting a version, so "the owner types a
/// brand-new key into existence" - the exact failure `25-43`'s own text warns against - is structurally
/// unavailable, not merely discouraged.
///
/// <para><b>Domain, not Application, deliberately.</b> This is a business rule ("a price may only be
/// published for a resource the product actually built"), the same status
/// <see cref="SubscriptionTierBands"/>'s own compile-time shape already holds for the seat ladder - not
/// a read-model or a UI concern, which is why it sits beside the constants it registers
/// (<see cref="SubscriptionTierBands.BaseSeatPriceKey"/>/<see cref="SubscriptionTierBands.ExtraSeatPriceKey"/>)
/// rather than in an Application-layer catalogue. Nothing here is deployment-specific (contrast
/// <c>IModuleEntryPointProvider</c>, which resolves a per-deployment address) - every deployment of
/// this codebase registers the identical keys, because the registration follows the code, not the
/// environment.</para>
///
/// <para><b>Every entry needs a short label too</b> - the platform owner's own publish screen has to
/// show something more legible than a raw key string when it lists what can be priced, and a second,
/// hand-maintained lookup table living in the console would drift from this one the first time either
/// changed without the other. One list, read by both the server-side validation and (via
/// <c>GetPricingForOwnerHandler</c>'s own read, extended by this item) the owner's own screen.</para>
/// </summary>
public static class PricedResourceKeys
{
    /// <summary>Every <see cref="PriceKey"/> a real charge site in this codebase reads today, with a
    /// short label for the platform owner's own publish screen. Add one entry here in the same change
    /// that wires a new charge site to <see cref="Application.Abstractions.IPriceCatalogRepository"/> -
    /// never the other way round.</summary>
    public static readonly IReadOnlyList<PricedResourceKeyDescriptor> All =
    [
        new(SubscriptionTierBands.BaseSeatPriceKey, "Business tier - base price (up to the included seats)"),
        new(SubscriptionTierBands.ExtraSeatPriceKey, "Business tier - price per seat beyond the included seats"),
    ];

    /// <summary><see langword="true"/> only for a key this registry actually lists - the check
    /// <c>PublishPriceVersionHandler</c> makes before ever minting a version, so a caller can never
    /// publish a price for a key nothing in this codebase reads.</summary>
    public static bool IsKnown(PriceKey key) => All.Any(descriptor => descriptor.Key == key);
}

/// <param name="Key">The opaque key a real charge site reads.</param>
/// <param name="Label">A short, human-readable description of what this key prices - shown on the
/// platform owner's own publish screen, never read by any charge site.</param>
public sealed record PricedResourceKeyDescriptor(PriceKey Key, string Label);
