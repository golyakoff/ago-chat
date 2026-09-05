using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetOperatorPresence;

/// <summary>`23-20`: see <see cref="GoOnline"/>'s own remarks - the identical "no `SiteId`, no
/// permission check" shape. `OperatorHub.SetAwayAsync` resolves <see cref="OperatorId"/> from the
/// connection's own JWT, exactly like `GoOnline`/`GoOffline` already do, so there is no "whose
/// presence" question for a caller to get wrong - an operator cannot name anyone else's id here
/// because nothing on this command accepts one.</summary>
public sealed record GoAway(OperatorId OperatorId);
