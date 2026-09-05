using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.GetOperatorPresence;

/// <summary>
/// `23-20`: backs `OperatorHub.GetMyPresenceAsync` - the console's own read of its current toggle
/// state, called once whenever the connection reports "connected" (a first connect and every
/// reconnect alike, `ago-console`'s `OperatorConnectionProvider`). Without this, a console reload or a
/// reconnect would have nothing to render the away control from except a locally-remembered React
/// state that a page reload discards - a control that can silently disagree with the truth it exists
/// to state honestly, exactly the failure mode `flows.md` 2.5 is about, just moved one level in.
///
/// A pure read, no `Result` wrapper: same reasoning as
/// <see cref="Ago.Chat.Application.UseCases.SetOperatorPresence.SetOperatorPresenceHandler"/>'s own
/// not-found path - the id came from a JWT this exact connection already authenticated with, so a
/// missing row is this codebase's own invariant broken, not a caller mistake to report gracefully.
/// </summary>
public sealed class GetOperatorPresenceHandler(IOperatorRepository operators)
{
    public async Task<OperatorStatus> HandleAsync(GetOperatorPresence query, CancellationToken cancellationToken)
    {
        var operatorEntity = await operators.GetByIdAsync(query.OperatorId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operator {query.OperatorId.Value} not found while reading its own presence.");

        return operatorEntity.Status;
    }
}
