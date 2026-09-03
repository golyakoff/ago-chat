using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeModuleForSite;

public sealed record RevokeModuleForSite(OperatorId RequestedBy, SiteId SiteId, string ModuleKey, string ProvisioningSecret);
