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

    /// <summary>
    /// `22-30`/`adr/0149` rule 2: "a lifecycle operation completes when the module proves it." Asks
    /// the module to erase everything it holds for this tenant and reads back what it found - never
    /// merely whether the call succeeded. Deliberately on this interface, authenticated by the
    /// deployment-wide <paramref name="provisioningSecret"/> like every other method here, rather than
    /// on <see cref="IModuleGateway"/> with a per-site <see cref="ModuleCredential"/>: the whole point
    /// of this call is to still reach a module for a site whose per-site credential may have been
    /// revoked, may have lapsed, or may never have existed (`22-30`'s own backlog names all three as
    /// cases this call must still answer for), which is exactly what the provisioning secret does not
    /// depend on.
    /// </summary>
    Task<TenantDataErasureResult> EraseTenantDataAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken);

    /// <summary>
    /// `22-31`: `GET .../tenant-data` - the read-only sibling of <see cref="EraseTenantDataAsync"/>'s own
    /// `DELETE` on the identical path, over the identical deployment-wide provisioning secret and for the
    /// identical reason - the backlog item's own Depends-on names `22-30`'s module gate explicitly:
    /// export needs the same reach to a tenant whose per-site <see cref="ModuleCredential"/> may be
    /// revoked, lapsed, or never issued, which is exactly what this channel (unlike
    /// <see cref="IModuleGateway"/>'s own per-site-credentialed one) does not depend on.
    ///
    /// <para><b>The module's own opaque bytes, not a parsed shape.</b> `adr/0149` rule 3 - chat never
    /// parses a module's data - decides the return type here: <see cref="ModuleTenantExportResult.Content"/>
    /// is a fully-received, seekable local stream the caller copies verbatim into its own export archive.
    /// See <see cref="ModuleTenantExportResult"/>'s own remarks for why this port hands back a local
    /// stream rather than the live network response.</para>
    ///
    /// <para><b>Resilience lives outside this method, applied by the caller.</b> The identical split
    /// <see cref="IModuleGateway"/>'s own remarks state for its own boundary ("an implementation is
    /// written as if the module always answers, and resilience is applied by wrapping it") - here that
    /// means <c>Ago.Chat.Worker.SiteExportArchiveWriter</c> wraps this one call in its own
    /// <c>Ago.Chat.Module.Modules.ModuleExportResiliencePipelines</c>, sized for a whole tenant's history
    /// rather than reusing <see cref="HttpModuleRegistrationGateway"/>'s usual "deliberately unwrapped"
    /// treatment of every other method here (this backlog item's own Answered section: a rare
    /// provisioning-channel call that must now carry more than an operator-issued command, so it earns
    /// the one exception).</para>
    /// </summary>
    Task<ModuleTenantExportResult> ExportTenantDataAsync(
        ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken);
}

/// <param name="TenantExisted">Whether the module reports it held a row for this tenant at all -
/// informational, the identical "not what a caller should branch on" status
/// <c>Ago.Calendar.Application.Abstractions.TenantErasureResult.TenantExisted</c>'s own remarks give
/// its wire-side counterpart.</param>
/// <param name="Confirmed">The module's own proof, read back over the wire: a fresh read of *its*
/// database found nothing left for this tenant. This is the fact <c>Ago.Chat.Worker.SiteErasureJob</c>
/// gates on - never <paramref name="TenantExisted"/>, and never merely "the HTTP call returned
/// 200".</param>
public readonly record struct TenantDataErasureResult(bool TenantExisted, bool Confirmed);

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

/// <summary>
/// `22-31`: one module's opaque export artifact for one tenant. <see cref="Content"/> is a fully
/// downloaded, seekable local file stream - never the live network response stream - so that
/// <see cref="Ago.Chat.Module.Modules.ModuleExportResiliencePipelines"/> can safely retry the whole call
/// on a transient failure: each attempt inside <c>HttpModuleRegistrationGateway.ExportTenantDataAsync</c>
/// downloads into its own fresh temp file and cleans it up on failure, so a retried attempt never leaves
/// a caller holding a half-written stream. The file is opened with <c>FileOptions.DeleteOnClose</c>, so
/// disposing this result (after its bytes have been copied into the caller's own archive entry) is what
/// removes the temp file - there is no separate cleanup step for a caller to forget.
/// </summary>
public sealed class ModuleTenantExportResult(int formatVersion, long sizeBytes, Stream content) : IAsyncDisposable
{
    /// <summary>The module's own format version for these bytes - opaque to chat (adr/0149 rule 3),
    /// carried only so <c>manifest.json</c> can record it.</summary>
    public int FormatVersion { get; } = formatVersion;

    /// <summary>The exact byte count, known upfront because the download is already complete by the time
    /// this result exists - unlike the live network response, whose length a chunked transfer may never
    /// state.</summary>
    public long SizeBytes { get; } = sizeBytes;

    public Stream Content { get; } = content;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
