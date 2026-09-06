namespace Ago.Chat.Domain;

/// <summary>
/// `24-10`: one row of <c>conversation_block_records</c> per act, never per state - <see
/// cref="Conversation.BlockedAt"/>/<see cref="Conversation.BlockedBy"/> already answer "is it blocked
/// right now, and by whom"; this enum answers the Done-when's other half, "both acts are recorded" -
/// a block and its later reversal are two separate, independently timestamped facts, not one row
/// updated in place. The same "current state is a flag, history is an append-only log" split
/// <c>erasure_records</c>/<c>access_records</c> already established for this codebase's other two
/// audit trails.
/// </summary>
public enum ConversationBlockRecordKind
{
    Blocked,
    Unblocked,
}
