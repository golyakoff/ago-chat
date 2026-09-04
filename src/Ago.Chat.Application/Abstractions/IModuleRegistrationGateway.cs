using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `22-11`: the module-side half of `EnableModuleForSite`, `RotateModuleCredential` and
/// `RevokeModuleForSite` - the port that actually reaches the module deployment named by
/// <see cref="EnabledModule.EntryPoint"/> and makes the row the module's own
/// `HmacModuleCallCredentialValidator`-shaped check reads. Its receiving half is a new, generic
/// endpoint each module product implements over `adr/0065`'s wire family - `POST/PUT/DELETE/GET
/// .../api/v1/module-registrations/{siteId}` - not a calendar- or faq-shaped call: this port takes an
/// opaque <see cref="ModuleKey"/> and a URL, exactly like <see cref="IModuleGateway"/> does, so Chat
/// still never learns what is on the other end of an <see cref="EnabledModule.EntryPoint"/>.
///
/// <para><b>A second, sibling port to <see cref="IModuleGateway"/>, not a third method on it.</b>
/// <see cref="IModuleGateway"/>'s own shape is "ask the module about a task and take its answer" -
/// every call there carries a <see cref="ModuleCredential"/> already trusted, minted per call from a
/// row that already exists. Provisioning is a different question entirely ("does a row exist / make
/// one exist"), authenticated by a different, deployment-wide secret
/// (<see cref="ModuleProvisioningSecret"/>) rather than a per-site one - folding both onto one
/// interface would mix two authentication mechanisms behind one abstraction for no reader's
/// benefit.</para>
///
/// <para><b>Every method throws <see cref="ModuleUnreachableException"/> on any failure</b> - the
/// identical "one exception, whatever the underlying cause" shape <see cref="IModuleGateway"/>'s own
/// remarks describe, reused rather than inventing a second failure vocabulary for a second gateway
/// that fails in the same ways (timeout, connection refused, non-2xx, malformed response). A 401 from
/// a wrong provisioning secret is one more thing this maps to the same exception - the caller (an
/// `EnableModuleForSite`-shaped handler) has nothing more specific to do about a rejected call than
/// about an unreachable one.</para>
/// </summary>
public interface IModuleRegistrationGateway
{
    /// <param name="displayName">`22-17`: an opaque, human-readable label for whoever
    /// <paramref name="module"/>'s <see cref="ModuleRegistrationTarget.SiteId"/> names - Chat's own
    /// <see cref="Site.Name"/>, carried along unopened. Not a fact about "calendar" or "faq": every
    /// module product may need *some* human-readable name for the account it is provisioning a row
    /// for, the same way it already needs the coordinates <see cref="ModuleRegistrationTarget"/>
    /// carries, so this is one more opaque string on an already-opaque contract, not a new kind of
    /// knowledge Chat is handing across the boundary. See <c>RegisterChatModuleHandler</c>'s own
    /// remarks on the calendar side for what it is used for there.</param>
    Task RegisterAsync(
        ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
        string displayName, CancellationToken cancellationToken);

    Task RotateAsync(
        ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
        CancellationToken cancellationToken);

    Task RevokeAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken);

    Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken);
}

/// <summary>Which module, which site, where to reach it - the coordinates every call above needs, and
/// nothing about a credential: unlike <see cref="EnabledModuleEndpoint"/>, this type is built before a
/// working <see cref="ModuleCredential"/> necessarily exists on either side (registration is what makes
/// one exist), so it deliberately does not carry one.</summary>
public sealed record ModuleRegistrationTarget(ModuleKey ModuleKey, SiteId SiteId, Uri EntryPoint);

/// <param name="Exists">Whether the module deployment holds a registration for this site at all.</param>
/// <param name="RegisteredAt">Unset when <paramref name="Exists"/> is <see langword="false"/>.</param>
/// <param name="HasCredentialInGracePeriod">Whether the module is currently honouring two credentials
/// for this site (a just-rotated previous one, still inside its overlap window) - surfaced so a
/// reconciliation check can tell "mid-rotation" apart from "settled".</param>
public readonly record struct ModuleRegistrationRemoteStatus(
    bool Exists, DateTimeOffset? RegisteredAt, bool HasCredentialInGracePeriod);
