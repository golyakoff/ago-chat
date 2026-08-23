using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetMyPermissions;

/// <summary>`5-08`: no participant/permission check of its own - "what are my own permissions" is
/// always answerable for whoever is asking, the same way asking for your own profile never needs an
/// authorization check beyond "you are authenticated as someone."</summary>
public sealed record GetMyPermissions(OperatorId OperatorId, SiteId SiteId);
