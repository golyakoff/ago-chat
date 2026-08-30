namespace Ago.Chat.Application.UseCases.GenerateReplyDraft;

/// <summary>
/// Bound from `ReplyDraft:*` config keys - the one non-secret, non-rate-limit knob this item needs:
/// how much of the conversation's own history to send. Deliberately small - `19-01`'s own "context
/// minimalism" scope line asks for "the conversation's own recent message history... and nothing
/// else", and a smaller window is also a cheaper prompt against a real per-call cost
/// (`resilience.md`'s "every call costs real money" framing, restated in this item's own Scope).
///
/// Not measured or load-tested - the same caveat every other options class in this codebase carries
/// for its own defaults (<see cref="AttachmentOptions"/>'s own remarks).
/// </summary>
public sealed class ReplyDraftOptions
{
    public const string SectionName = "ReplyDraft";

    /// <summary>How many of the conversation's most recent messages to include, oldest first once
    /// re-ordered - enough for the LLM to see the shape of the exchange, not the conversation's whole
    /// history. `GenerateReplyDraftHandler` reads this many rows and this many only from
    /// `IConversationReadStore.GetHistoryAsync`; a longer conversation's earlier turns are never
    /// fetched, so they cannot leak into the prompt even by accident.</summary>
    public int HistoryMessageCount { get; set; } = 20;
}
