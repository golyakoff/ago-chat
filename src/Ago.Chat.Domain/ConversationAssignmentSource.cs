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
/// <para><b>Only two members today.</b> `23-03`'s own Scope names four: this pair, plus <c>Taken</c>
/// (`23-04`, an operator's own deliberate claim once that path is reachable and distinguishable from an
/// automatic one) and <c>Additional</c> (`23-05`, the penalty-period path). Adding either now, unused,
/// would be exactly the premature column `data-model.md`'s "an index arrives with its first real
/// reader" discipline warns against, one level up - a member arrives with its first real writer.</para>
/// </summary>
public enum ConversationAssignmentSource
{
    /// <summary>The assignment engine gave this conversation to an operator with room - the two
    /// `Ago.Chat.Worker` claimers (`SkipLockedAssignmentClaimer`, `RedisLockAssignmentClaimer`).
    ///
    /// <para>`23-03`: also written, for now, by a human claiming a conversation through
    /// <c>AssignConversationHandler</c> (behind <c>OperatorHub.JoinConversationAsync</c>) - see that
    /// handler's own remarks for why. That path has no reachable UI yet (`23-04`'s own Goal: "the path
    /// exists and cannot be reached"), so today nothing can tell an automatic assignment and a manual
    /// one apart at the domain-event level either, and this enum does not invent a distinction the code
    /// cannot yet draw. `23-04` splits it into its own <c>Taken</c> member once a real, reachable act
    /// exists to attach it to.</para>
    /// </summary>
    Assigned,

    /// <summary>An already-assigned conversation moved from one operator to another -
    /// `TransferConversationHandler`. Closes the departing operator's interval and opens the
    /// receiving operator's in the same transaction; see that handler's own remarks.</summary>
    Transferred,
}
