namespace Ago.Chat.Contracts;

/// <summary>
/// `23-59`/`adr/0147`: "a contact recorded through <c>RecordVisitorContactDetail</c> emits an
/// integration event through the outbox, in the same transaction as the write... Chat does not know
/// whether anybody is listening." The identical cross-boundary shape <see cref="RoleAssignmentsChanged"/>
/// and <see cref="ContactVisibilityChanged"/> already establish: chat publishes unconditionally,
/// ignorant of whether a calendar (or any other product) exists, and the fact is complete in itself -
/// no delta, nothing a consumer has to merge against a prior state.
///
/// <para><b>Fired for every <see cref="Ago.Chat.Domain.VisitorContactDetail"/> ever written</b> -
/// operator-recorded and visitor-recorded alike (`RecordVisitorContactDetailHandler`'s two entry
/// points), and every <see cref="Ago.Chat.Domain.VisitorContactDetailKind"/>, not only
/// <c>Phone</c>. Chat has no reason to know that only a phone number has anywhere to go on the far
/// side - <c>adr/0065</c> decision 2 keeps chat ignorant of what a module even is, and filtering by
/// kind here would be exactly the "chat learns what the calendar needs" leak that decision forbids.
/// The calendar's own consumer is where <c>Kind != "Phone"</c> is discarded, the same way
/// <c>ModuleQuantityGrantedConsumer</c> discards a grant for a module key that is not its own.</para>
///
/// <para><b><see cref="ContactDetailId"/> is this event's identity, not <see cref="SiteId"/> or the
/// phone value.</b> There is exactly one fact this id will ever describe *as this event saw it* - which
/// is what lets the far side's idempotency key be this id rather than a phone number: two different
/// contacts that happen to share a phone must stay two rows (`adr/0147`'s own "a phone that matches an
/// existing customer does not merge"), and keying dedup on the id rather than the value is what makes
/// that automatic rather than a special case the consumer has to remember.
///
/// <b>`25-58` gave <see cref="Ago.Chat.Domain.VisitorContactDetail"/> its first two mutation methods -
/// this event is published only once, at record time, and is never republished on a later edit or
/// assessment change.</b> A far-side consumer keyed on this id per the paragraph above would need to
/// decide how to react to a value that changed under an id it was told never would; nothing in this
/// codebase answers that today (no consumer exists yet - AGO Calendar is still planned, per `ago-root`'s
/// own `CLAUDE.md`), so this is stated here as a real, open gap rather than guessed at with a republish
/// shape nothing downstream exists to receive.</para>
///
/// <para><b>Published twice for the same underlying row, deliberately: once live, once (if ever)
/// through <c>ContactCarryoverBackfill</c>'s own retroactive pass.</b> Both publishers stage the
/// identical <see cref="ContactDetailId"/>, so a contact collected before a tenant ever had the
/// calendar and later carried over is indistinguishable, at the far side, from one that arrived after
/// the grant - the same envelope, the same idempotency key, whichever publisher happened to send
/// it.</para>
///
/// <para>No display name, no free-text label - only what the far side needs to decide whether this is
/// a phone number and, if so, what it is: the same no-body-crosses-the-broker discipline
/// <see cref="RoleAssignmentsChanged"/>'s own remarks state for a permission set.</para>
/// </summary>
public sealed record ContactCollected(
    Guid ContactDetailId,
    Guid SiteId,
    string Kind,
    string Value,
    DateTimeOffset RecordedAt,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
