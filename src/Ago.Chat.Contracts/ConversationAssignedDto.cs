namespace Ago.Chat.Contracts;

/// <summary>The realtime protocol's wire shape for `4-02`'s assignment notification
/// (api-design.md: "payload shapes live in Ago.Chat.Contracts... versioned with the same
/// additive-only rule as integration events"). Pushed to both the visitor and the newly-assigned
/// operator as `"ConversationAssigned"`.</summary>
public sealed record ConversationAssignedDto(Guid ConversationId, Guid OperatorId, DateTimeOffset AssignedAt);
