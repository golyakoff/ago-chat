using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RemoveTeamMessage;

/// <summary>
/// `23-33`: "the tenant can remove a message from the team chat" - unlike
/// <see cref="Application.UseCases.SendTeamMessage.SendTeamMessage"/>, this is a genuine capability,
/// not something every operator of the site may do unconditionally
/// (<see cref="RemoveTeamMessageHandler"/>'s own remarks explain exactly which permission gates it and
/// why). <see cref="SiteId"/> is still the caller's own operator claim, never a route segment or a
/// body value, for the identical reason every other team-chat command reads it that way - the room
/// this acts on is always the caller's own site's.
/// </summary>
public sealed record RemoveTeamMessage(SiteId SiteId, OperatorId RequestedBy, TeamMessageId TeamMessageId);
