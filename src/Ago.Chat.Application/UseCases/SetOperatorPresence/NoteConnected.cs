using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.SetOperatorPresence;

/// <summary>`23-20`: the connect path's own command, separate from <see cref="GoOnline"/> precisely so
/// the two can mean different things - see <see cref="Domain.Operator.NoteConnected"/>'s own remarks
/// for why a passive "a connection now exists" must not carry the same authority as the operator's own
/// explicit "I want to be online". `OperatorHub.OnConnectedAsync` is this command's only caller.</summary>
public sealed record NoteConnected(OperatorId OperatorId);
