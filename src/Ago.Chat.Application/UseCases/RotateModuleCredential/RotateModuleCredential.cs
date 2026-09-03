using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RotateModuleCredential;

/// <summary>
/// `22-11`'s own "rotate without downtime" Done-when. Unlike `EnableModuleForSite`,
/// <see cref="NewCredential"/> is minted by this handler with the platform CSPRNG rather than accepted
/// as raw operator input - see <see cref="RotateModuleCredentialHandler"/>'s own remarks for why that
/// asymmetry with `EnableModuleForSite` is deliberate rather than an inconsistency, and
/// <see cref="ProvisioningSecret"/>'s own remarks on `EnableModuleForSite` for why this call needs one
/// too.
/// </summary>
public sealed record RotateModuleCredential(OperatorId RequestedBy, SiteId SiteId, string ModuleKey, string ProvisioningSecret);

public readonly record struct ModuleCredentialRotated(ModuleCredential NewCredential);
