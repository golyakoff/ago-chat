using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-86`/`adr/0159`: "the mapping from an option to the entitlement it grants is deployment
/// configuration, resolved by key - the shape `adr/0154` established for module entry points and
/// `23-102` for module permissions" (`adr/0159`'s own Decision). This port is that shape's third
/// instance: <see cref="IModuleEntryPointProvider"/> resolves a <see cref="ModuleKey"/> to where the
/// module lives, <see cref="IModulePermissionsProvider"/> resolves it to what it needs, and this
/// resolves a <see cref="BillingOptionKey"/> - the price list's own vocabulary, never named here - to
/// the <see cref="ModuleKey"/> that turning it on actually means.
///
/// <para><b>Never a fixed set of properties, for the identical reason.</b> An options class with one
/// property per known option key would be the exact literal `Ago.Chat.Architecture.Tests` exists to
/// catch (`ModuleKey`'s own remarks, `adr/0065` decision 2) - a lookup by an opaque key, resolved
/// against whatever the deployment declares, keeps this assembly ignorant of which options exist while
/// still letting the deployment supply the mapping.</para>
///
/// <para><b>Carries no price.</b> "The deployment declares what an option turns on, never what it
/// costs" (`23-86`'s own Scope) - prices live in the private `ago-business` repository; this port
/// answers only "what does buying this turn on", the identical no-price discipline
/// <see cref="Contracts.ModuleQuantityGranted"/>'s own remarks already state for the event a grant
/// through this mapping eventually publishes.</para>
///
/// <para><b><see langword="null"/> is a legible refusal, never a hidden default</b> - the identical
/// posture <see cref="IModuleEntryPointProvider"/>'s own remarks state: an option key this deployment
/// has not mapped to an entitlement fails the grant loudly (`SubscriptionRenewalApplier`'s own remarks
/// on why that failure is a thrown exception, not a swallowed no-op) rather than silently granting
/// nothing.</para>
/// </summary>
public interface IBillingOptionEntitlementProvider
{
    /// <summary><see langword="null"/> when this deployment has declared no entitlement mapping for
    /// <paramref name="optionKey"/>.</summary>
    ModuleKey? TryGet(BillingOptionKey optionKey);
}
