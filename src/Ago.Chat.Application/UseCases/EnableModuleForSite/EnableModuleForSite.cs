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
public sealed record EnableModuleForSite(
    OperatorId RequestedBy, SiteId SiteId, string ModuleKey, IReadOnlyList<string> TriggerWords, string EntryPoint);
