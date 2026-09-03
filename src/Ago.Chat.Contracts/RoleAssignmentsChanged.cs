namespace Ago.Chat.Contracts;

/// <summary>
/// `22-05`/`adr/0093`: the fact that one Keycloak subject's permission set for one site is now exactly
/// <see cref="Permissions"/> - the account side's own role catalogue, resolved. A snapshot of the
/// *current, complete* set, not a diff: a consumer that upserts its own projection to whatever this
/// says needs no merge logic and is naturally idempotent under at-least-once redelivery (`messaging.md`
/// - "prefer naturally idempotent writes"), which a grant/revoke pair of events would not be (delivery
/// order is only guaranteed per <see cref="ExternalSubjectId"/>, never globally - `CLAUDE.md` rule 6 -
/// so a consumer that applied "add" and "remove" facts out of order could land on the wrong state
/// forever with no way to notice).
///
/// <para><b>Fired whenever the write side changes what this fact would answer</b>: site registration
/// (the owner's two seeded roles), an invite redemption (the new operator's one role), and an operator
/// removal (published with an empty <see cref="Permissions"/> - revocation is this same fact becoming
/// "nothing", not a different kind of event). There is no role-grant/revoke endpoint yet
/// (`adr/0016`'s own "deferred past the seed script" - unchanged by this item), so these three call
/// sites are exhaustive today; a future one only has to publish the same shape.</para>
///
/// <para><b>An empty <see cref="ExternalSubjectId"/> is impossible by construction, never sent</b>: an
/// operator with no linked identity yet (an unredeemed invite) grants nothing to project, because
/// nothing can act on it - the fact would have no subject to attach to. Every publisher below checks
/// this before enqueuing.</para>
///
/// <para><b>No display name, no email - only what a permission check needs.</b> The same
/// no-body-crosses-the-broker discipline `AttachmentConfirmed`'s own remarks state for a message: this
/// event exists to answer "may this subject do X in this tenant", not to describe the person.</para>
/// </summary>
public sealed record RoleAssignmentsChanged(
    string ExternalSubjectId,
    Guid SiteId,
    IReadOnlyList<string> Permissions,
    Guid CorrelationId,
    DateTimeOffset OccurredAt);
