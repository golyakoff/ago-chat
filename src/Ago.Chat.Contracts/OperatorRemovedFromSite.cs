namespace Ago.Chat.Contracts;

/// <summary>
/// `13-03`: an operator was removed from their site (<c>Operator.Remove</c>) - published through the
/// outbox (a real committed state change, <c>Ago.Chat.Domain.OperatorRemoved</c>'s own remarks),
/// consumed by `Ago.Chat.Worker`'s <c>OperatorRemovedConsumer</c> to release this operator's `Assigned`
/// conversations back to `Waiting` via the existing `OperatorConversationReleaser`.
///
/// <para>Named <see cref="OperatorRemovedFromSite"/>, not the bare <c>OperatorRemoved</c> the domain
/// event already uses - the identical <c>ConversationAssigned</c>/<c>ConversationAssignedToOperator</c>
/// naming split for the identical reason: the mapper that constructs this contract from
/// <c>Ago.Chat.Domain.OperatorRemoved</c> needs both types in scope at once, and a shared bare name
/// would be ambiguous to reference unqualified from inside it.</para>
/// </summary>
public sealed record OperatorRemovedFromSite(Guid OperatorId, Guid SiteId, Guid CorrelationId, DateTimeOffset OccurredAt);
