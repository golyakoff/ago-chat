using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-72`: the backlog item's own Scope, verbatim - "every role change is recorded - who, whom, from
/// what to what, when." A receipt with no aggregate behind it, the same "written once, by the handler
/// that decided the change, never touched again" shape <see cref="IAccessRecordRepository"/> already
/// establishes for its own family - but a deliberately separate table, not a widened
/// <c>access_records</c>. <see cref="AccessRecordKind"/>'s own remarks state it is "the defensible set
/// the backlog item names, not one member per permission-gated endpoint" - every existing member is
/// either the platform owner acting across a tenant boundary, or the one cross-conversation read that
/// crosses a boundary of its own (`18-07`). An administrator changing a colleague's role inside their
/// own tenant, checked by the same <see cref="IPermissionChecker"/> gate every ordinary write in this
/// codebase already goes through, crosses no boundary `access_records` exists to police - it earns its
/// own record instead of stretching that enum past the set it was deliberately kept to.
///
/// <para><b>Must run inside the caller's own ambient transaction</b>
/// (<see cref="IUnitOfWork.BeginTransactionAsync"/>) - the same contract
/// <see cref="IOperatorRoleRepository.ReplaceRoleAsync"/> states, so the record commits or rolls back
/// atomically with the role swap it describes. This is also why <c>RoleChangeRecordRepository</c>'s own
/// implementation writes through the same <c>AgoChatDbContext</c> every other participant of that
/// transaction shares, unlike <c>AccessRecordRepository</c>'s deliberate fresh
/// <c>NpgsqlDataSource</c> connection - that table's own writes are never part of a wider transaction
/// (`IAccessRecordRepository`'s own remarks: "only a successful, boundary-crossing access ever calls
/// this", a fact already settled by the time it is recorded), this one's whole point is failing
/// together with the write it documents.</para>
///
/// <para><b>Write-only today, deliberately.</b> This item's own Done-when asks that a change be
/// recorded, not that the console can browse the record - unlike `access_records`, which needed a
/// reader from its own first item (`24-12`'s tenant-facing report). Building a browsing screen nobody
/// asked for yet is exactly the scope this codebase's own rule 15 warns against; a read side is real,
/// buildable follow-up once an actual caller needs one, the same "grow this port when a second question
/// arrives" discipline every other narrow port in this codebase already follows.</para>
/// </summary>
public interface IRoleChangeRecordRepository
{
    Task RecordAsync(RoleChangeRecordToWrite record, CancellationToken cancellationToken);
}

/// <summary>One role change to be recorded - <paramref name="PreviousRoleNames"/> is the operator's
/// *whole* previous role set, not a single value, because an operator can hold more than one role at
/// once (the account's own founder holds both seeded roles from registration,
/// <c>SiteRegistrationRepository</c>'s own remarks) - collapsing that to one name would misreport a
/// dual-role operator's change as starting from a role they only partly held. <paramref name="NewRoleName"/>
/// is singular: <c>ChangeOperatorRoleHandler</c> replaces a colleague's whole assignment with exactly
/// one named role, the same single-role-per-invite shape invite redemption already establishes for a
/// freshly created operator - see that handler's own remarks for why this item does not build a way to
/// hold two roles at once for anybody but the account's own founder.
///
/// <para><b>`25-41`: <paramref name="ChangedByOperatorId"/> is nullable, widened from this record's
/// own original required shape</b> - every change until this item was a human's own act
/// (<c>ChangeOperatorRoleHandler</c>'s own caller, always a real, permission-checked operator). An
/// automatic demotion (<c>AdministratorLimitEnforcer</c>, triggered by a lapse or a downgrade, never by
/// a request) has no operator behind it to name honestly - attributing it to whoever happened to
/// trigger the billing event that caused it (a renewal job with no human caller at all, in the
/// <see cref="Ago.Chat.Application.UseCases.ProcessSubscriptionRenewal.ProcessSubscriptionRenewalHandler"/>
/// case) would misreport who acted, the same care <see cref="Ago.Chat.Domain.Operator.ExternalSubjectId"/>'s
/// own optionality already takes for "nothing to attribute this to" rather than inventing a
/// placeholder. <see langword="null"/> reads, honestly, as "the system, not a person, made this
/// change."</para></summary>
public sealed record RoleChangeRecordToWrite(
    Guid Id,
    SiteId SiteId,
    OperatorId? ChangedByOperatorId,
    OperatorId ChangedOperatorId,
    IReadOnlyList<string> PreviousRoleNames,
    string NewRoleName,
    DateTimeOffset ChangedAt);
