using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.SetOperatorPresence;

/// <summary>
/// `4-06`: closes the gap `RegisterSiteHandler`'s own comment named but never built - "Offline, not
/// Online - this operator has not connected yet (presence, Stage 3, is what actually flips this once
/// their console session opens)". Nothing did. The assignment engine (`SkipLockedAssignmentClaimer`,
/// `RedisLockAssignmentClaimer`) has always required <see cref="Domain.OperatorStatus.Online"/>, so
/// every runtime-created operator - minted demo tenants and every real registration alike - was
/// permanently unassignable; only `ago-deploy/seed/create-demo-tenant.sh`'s raw SQL ever wrote
/// `Online` for its own hand-seeded rows, which is why assignment only ever looked like it worked.
///
/// <para>No permission check, unlike every other handler in this codebase: every other command acts
/// on a resource named by the caller (a conversation id, a site id) that must be checked against the
/// caller's own claims. This command has no such resource - <see cref="GoOnline.OperatorId"/>/
/// <see cref="GoOffline.OperatorId"/> already <em>is</em> the caller's own identity, resolved by
/// `OperatorHub` from the connection's JWT before either method is ever invoked. There is no "whose
/// presence" question left to ask.</para>
///
/// <para>An <see cref="InvalidOperationException"/> on a missing row, not a <c>Result</c> failure:
/// every other handler's not-found path exists because a caller-supplied id might name someone else's
/// resource or nothing at all. Here the id came from a JWT `OperatorHub` already used to authenticate
/// this exact connection - a missing row is not a caller mistake to report, it is this codebase's own
/// invariant broken, and failing loudly is the right reaction to that, not a quiet `Result.Failure`
/// a hub lifecycle method has no client request to attach it to anyway.</para>
/// </summary>
public sealed class SetOperatorPresenceHandler(IOperatorRepository operators)
{
    public async Task GoOnlineAsync(GoOnline command, CancellationToken cancellationToken)
    {
        var operatorEntity = await operators.GetByIdAsync(command.OperatorId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operator {command.OperatorId.Value} not found while recording a live connection.");

        operatorEntity.GoOnline();
        await operators.SaveAsync(operatorEntity, cancellationToken);
    }

    public async Task GoOfflineAsync(GoOffline command, CancellationToken cancellationToken)
    {
        var operatorEntity = await operators.GetByIdAsync(command.OperatorId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operator {command.OperatorId.Value} not found while recording its last connection dropping.");

        operatorEntity.GoOffline();
        await operators.SaveAsync(operatorEntity, cancellationToken);
    }

    /// <summary>`23-20`: `OperatorHub.OnConnectedAsync`'s own caller, in place of <see cref="GoOnlineAsync"/> -
    /// see <see cref="Domain.Operator.NoteConnected"/>'s own remarks for why a passive connect must not
    /// carry the authority to cancel a deliberate <see cref="Domain.OperatorStatus.Away"/>.</summary>
    public async Task NoteConnectedAsync(NoteConnected command, CancellationToken cancellationToken)
    {
        var operatorEntity = await operators.GetByIdAsync(command.OperatorId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operator {command.OperatorId.Value} not found while recording a live connection.");

        operatorEntity.NoteConnected();
        await operators.SaveAsync(operatorEntity, cancellationToken);
    }

    /// <summary>`23-20`: the console's own "I'm stepping away" action, behind `OperatorHub.SetAwayAsync(true)`.</summary>
    public async Task GoAwayAsync(GoAway command, CancellationToken cancellationToken)
    {
        var operatorEntity = await operators.GetByIdAsync(command.OperatorId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Operator {command.OperatorId.Value} not found while recording they are stepping away.");

        operatorEntity.GoAway();
        await operators.SaveAsync(operatorEntity, cancellationToken);
    }
}
