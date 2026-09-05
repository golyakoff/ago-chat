namespace Ago.Chat.Domain;

/// <summary>
/// `23-03`: how a <see cref="ConversationAssignmentInterval"/> came about - a closed vocabulary, the
/// same "no free text, nothing to parse back apart" reasoning <see cref="ConversationOutcome"/>'s own
/// remarks give for itself. This is the raw fact only: `decisions.md` §2's naming amendment is explicit
/// that no screen may print a member of this enum verbatim ("forced" is named there as the label to
/// never show a person judged on it) - a reader sees only a **standard** conversation or an
/// **additional** one, computed from this column's own interval overlap against `operators.capacity`,
/// never stored as a flag here.
///
/// <para><b>Three members today, a fourth still to come.</b> `23-03`'s own Scope named four: this pair,
/// plus <see cref="Taken"/> (`23-04`, below) and <c>Additional</c> (`23-05`, the penalty-period path).
/// Adding either unused would have been exactly the premature column `data-model.md`'s "an index
/// arrives with its first real reader" discipline warns against, one level up - a member arrives with
/// its first real writer, which is why <see cref="Taken"/> was not added until this item gave it
/// one.</para>
/// </summary>
public enum ConversationAssignmentSource
{
    /// <summary>The assignment engine gave this conversation to an operator with room - the two
    /// `Ago.Chat.Worker` claimers (`SkipLockedAssignmentClaimer`, `RedisLockAssignmentClaimer`).
    ///
    /// <para>`23-03`-`23-04`: also written, briefly, by a human claiming a conversation through
    /// <c>AssignConversationHandler</c> (behind <c>OperatorHub.JoinConversationAsync</c>), for the
    /// entire window in which that path had no reachable UI (`23-04`'s own Goal: "the path exists and
    /// cannot be reached") and nothing could tell an automatic assignment and a manual one apart at the
    /// domain-event level either. `23-04` gave that path its own reachable act and its own
    /// <see cref="Taken"/> value; every interval <c>AssignConversationHandler</c> opens from that item
    /// onward carries <see cref="Taken"/>, never this member - see that handler's own remarks.</para>
    /// </summary>
    Assigned,

    /// <summary>An already-assigned conversation moved from one operator to another -
    /// `TransferConversationHandler`. Closes the departing operator's interval and opens the
    /// receiving operator's in the same transaction; see that handler's own remarks.</summary>
    Transferred,

    /// <summary>
    /// `23-04`: an operator's own deliberate claim of a `Waiting` conversation - `AssignConversationHandler`,
    /// reached either through `OperatorHub.JoinConversationAsync` (navigating to a conversation the
    /// rail now links to) or through the new `POST /api/v1/conversations/{id}/claim` route `/admin` and
    /// `/search` use. `decisions.md` §2's whole point: this is a fact about how a person chose to act,
    /// genuinely distinct from <see cref="Assigned"/> (the engine decided) even though both leave a
    /// conversation `Assigned` and both now charge `operators.active_chats` - see
    /// <c>IOperatorCapacity.ClaimAsync</c>'s own remarks for why the write is unconditional here despite
    /// being conditional (<c>TryClaimAsync</c>) for <see cref="Assigned"/>.
    ///
    /// <para>Never printed verbatim on a screen a person is judged on - the naming note at the top of
    /// this type's own remarks applies to this member exactly as it does to the other three.</para>
    /// </summary>
    Taken,
}
