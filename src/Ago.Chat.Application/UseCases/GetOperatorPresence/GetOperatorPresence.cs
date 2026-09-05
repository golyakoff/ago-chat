using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorPresence;

/// <summary>`23-20`: "what is my own presence right now" - the read half of `SetOperatorPresence`,
/// split into its own use case folder rather than added there, the same `Get*`/`Set*` split this
/// codebase already draws elsewhere (`GetVisitorPresence` beside the message-send use cases). No
/// permission check, for the identical reason `SetOperatorPresenceHandler`'s own doc comment gives:
/// <see cref="OperatorId"/> already <em>is</em> the caller's own identity, resolved by `OperatorHub`
/// from the connection's JWT - there is no "whose presence" question to ask.</summary>
public sealed record GetOperatorPresence(OperatorId OperatorId);
