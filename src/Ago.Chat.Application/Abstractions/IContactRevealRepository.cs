using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-11`/`decisions.md` §5: the write side of "a reveal is an act that leaves a record" - one row
/// per deliberate unmasking of a visitor contact detail, the same family
/// <see cref="IAccessRecordRepository"/> already establishes (a receipt with no aggregate behind it,
/// raw Npgsql end to end - <c>ContactRevealRepository</c>'s own remarks give the identical "no
/// business invariant beyond one row per event" reasoning).
///
/// <para><b>A dedicated table, not a widened <see cref="AccessRecordKind"/> - the decision this item's
/// own brief asked to be made and argued, not defaulted.</b> `access_records` names the defensible set
/// of reads that cross a *tenant or conversation* boundary an operator would not otherwise have
/// crossed (`24-12`'s own Scope; `adr/0113`'s own Consequences: "the set is named, not derived from a
/// rule"). A reveal crosses no such boundary - the revealing operator already holds
/// <see cref="Permission.ConversationRead"/> on this exact conversation, the identical permission that
/// already lets them read every other field the list response carries unmasked. What a reveal records
/// is not "an access nothing else gates" but "one specific field this tenant's own setting chose to
/// hide from its usual reader, on purpose, for attribution" - a narrower, product-specific fact
/// (`decisions.md` §5's own counter, "reveal counts belong in an audit view, never in the report a
/// person is judged on") that `access_records`' generic, cross-boundary framing does not carry and
/// should not be made to carry: commingling a potentially frequent, ordinary-operator-triggered event
/// into a table whose whole value is being a short, cross-boundary list would dilute the one report
/// `24-12` built this table to answer ("did anyone read this visitor's history from outside it") with
/// a volume of rows that answer a different question entirely.</para>
///
/// <para>Written by <c>RevealVisitorContactDetailHandler</c> (`Ago.Chat.Application`) - the acting
/// operator's id is already a field on its own command, the same single-layer-writes-it shape
/// `adr/0113`'s own remarks describe for `GetVisitorHistoryHandler`'s half of `access_records` (there
/// is no platform-owner caller here, so this port needs no second write site).</para>
/// </summary>
public interface IContactRevealRepository
{
    Task RecordAsync(ContactRevealToWrite reveal, CancellationToken cancellationToken);

    /// <summary>The tenant's own read-back - this item's own Done-when: "the tenant can read the
    /// reveal record, and the screen says what it is for and what it is not." Keyset by <c>id</c>
    /// descending (newest first), the same convention <see cref="IAccessRecordRepository.ListForSiteAsync"/>
    /// already uses; <paramref name="beforeId"/> <see langword="null"/> means the first page.</summary>
    Task<ContactRevealPage> ListForSiteAsync(SiteId siteId, Guid? beforeId, int limit, CancellationToken cancellationToken);
}

/// <summary>One reveal to be recorded - who revealed what, when, and which surface asked
/// (`23-11`'s own Scope, in those words). Deliberately carries no
/// <see cref="Domain.VisitorContactDetail.Value"/> - the same "record that a read happened, not what
/// was returned" discipline <see cref="AccessRecordToWrite"/>'s own remarks already state for its own
/// table, applied to this one.
///
/// <para><paramref name="SiteId"/>/<paramref name="ContactDetailId"/> carry no foreign key at the
/// storage layer (<c>ContactRevealEntityConfiguration</c>'s own remarks) - the same <c>adr/0111</c>/
/// <c>adr/0112</c>/<c>adr/0113</c> mechanism reused for the identical reason: a record of who revealed
/// this visitor's contact must survive both `SiteErasureJob`'s own site deletion and `23-08`'s own
/// conversation-scoped contact-detail erasure, or the one question this record exists to answer
/// ("who revealed this before it was deleted") would have its evidence destroyed by the very process
/// the question is about.</para>
/// </summary>
public sealed record ContactRevealToWrite(
    Guid Id,
    DateTimeOffset OccurredAt,
    SiteId SiteId,
    Guid ConversationId,
    Guid ContactDetailId,
    OperatorId OperatorId,
    string Surface);

/// <summary>One row, read back for a tenant's own report - the same fields <see cref="ContactRevealToWrite"/>
/// wrote, nothing more (in particular, never the contact detail's own value - see that type's own
/// remarks).</summary>
public sealed record ContactRevealItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid ContactDetailId,
    Guid OperatorId,
    string Surface);

/// <summary>One keyset page of <see cref="IContactRevealRepository.ListForSiteAsync"/> - the same
/// shape every other keyset read in this codebase returns (<c>NextBeforeId</c> <see langword="null"/>
/// once the oldest row has been reached).</summary>
public sealed record ContactRevealPage(IReadOnlyList<ContactRevealItem> Items, Guid? NextBeforeId);
