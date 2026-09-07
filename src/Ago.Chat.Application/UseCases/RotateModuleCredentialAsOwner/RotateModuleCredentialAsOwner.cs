using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RotateModuleCredentialAsOwner;

/// <summary>
/// `23-83`/`adr/0151`: the platform owner's own half of `22-11`'s "rotate without downtime" - moved
/// here, not merely reopened, once the tenant's own <c>RotateModuleCredential</c> stopped existing as
/// a route (<see cref="Api.Modules.ModuleEndpoints"/>'s own remarks on why). Deliberately a separate
/// command/handler rather than a nullable-<see cref="OperatorId"/> branch on the deleted tenant
/// command - the identical reasoning <see cref="EnableModuleForSiteAsOwner.EnableModuleForSiteAsOwner"/>'s
/// own remarks give for its own sibling: the fact that authorizes this call
/// (<c>RequirePlatformOwner</c> on the route) is not a fact <see cref="Abstractions.IPermissionChecker"/>
/// could ever check, so this command carries no <see cref="OperatorId"/> to check one against.
/// </summary>
/// <remarks><c>adr/0150</c>'s own amendment, extended: carries no <c>ProvisioningSecret</c> either -
/// the platform owner's caller never holds `adr/0095`'s deployment-wide secret at all.
/// <see cref="RotateModuleCredentialAsOwnerHandler"/> reads it from
/// <see cref="Abstractions.IModuleProvisioningSecretProvider"/> instead, the same source
/// <c>EnableModuleForSiteAsOwnerHandler</c>/<c>RevokeModuleForSiteAsOwnerHandler</c> already
/// use.</remarks>
public sealed record RotateModuleCredentialAsOwner(SiteId SiteId, string ModuleKey);

public readonly record struct ModuleCredentialRotatedByOwner(ModuleCredential NewCredential);
