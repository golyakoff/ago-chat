using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.EnableModuleForSite;

/// <summary>
/// `20-07`: "the registry - one row saying site X has module K enabled, and the entry point rendered
/// from it" (backlog item's own Scope). Raw strings in, exactly like every other command that carries a
/// value object's raw input (`SendVisitorMessage`'s own <c>ContentKind</c>/<c>Payload</c>) - the
/// handler is where an <c>ArgumentException</c> from <see cref="ModuleKey"/>'s or <see cref="Uri"/>'s
/// own constructor becomes a caller-facing <c>Result</c> failure instead of an unhandled throw.
///
/// <para>No tenant-facing admin UI or endpoint exists for this yet (backlog item's own "out of scope" -
/// an internal HTTP endpoint is optional/nice-to-have). This command is exercised directly by tests and
/// is ready to sit behind one whenever that endpoint is built.</para>
/// </summary>
/// <param name="RequestedBy">`17-01`'s tenant-scope rule (`TenantScopeTests`): every use case that
/// takes a <see cref="SiteId"/> either checks <c>IPermissionChecker</c> or is listed as an argued
/// exemption. Enabling a module for a site is a site-configuration write - the same category
/// <c>UpdateWidgetConfigHandler</c> gates with <see cref="Permission.SiteConfigure"/>, reused here
/// rather than inventing a module-specific permission this item's own scope names no other caller
/// for.</param>
/// <param name="Credential">`22-02`: the shared secret this site's module call proves itself with -
/// provided here exactly the way <paramref name="EntryPoint"/> already is, because both are
/// coordinates configured once on this side and once again, out of band, on the module deployment's
/// own side. See <see cref="ModuleCredential"/>'s own remarks for what this value does and does not
/// guarantee.</param>
/// <param name="ProvisioningSecret">`22-11`: proves this call may provision on the module deployment's
/// own behalf - see <see cref="ModuleProvisioningSecret"/>'s own remarks. Never persisted: this
/// handler uses it once, to make the module-side registration real, and then discards it - the row
/// this command produces on this side carries only <paramref name="Credential"/>.</param>
public sealed record EnableModuleForSite(
    OperatorId RequestedBy, SiteId SiteId, string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint,
    string Credential, string ProvisioningSecret);
