using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `26-29`: one row of <see cref="IConversationReadStore.GetLatestMessagesAsync"/> - a plain
/// projection of the <c>messages</c> table's own latest row for one conversation, the same
/// "read store returns rows, not aggregates" shape <see cref="MessageHistoryItem"/> and
/// <see cref="ConversationSummaryItem"/> already establish (adr/0004).
///
/// <para><b>Raw, undecided facts - not yet a preview.</b> This type answers only "what did the
/// database actually hold": the message's own body text, when it was written, and the two signals
/// that say whether that body text is a fair one-line summary at all - <see cref="ContentKind"/>
/// (non-<see langword="null"/> for a structured message, e.g. a module step) and
/// <see cref="AttachmentId"/> (non-<see langword="null"/> for a message that references a file).
/// Truncating the body and deciding which content kinds get a text preview at all is
/// <c>GetOperatorQueueHandler</c>'s own call (its own <c>ToSummary</c> remarks have the reasoning) -
/// this read-store adapter is Infrastructure, and rendering policy does not belong here any more than
/// it belongs in <see cref="MessageHistoryItem"/>'s own raw <see cref="MessageHistoryItem.Payload"/>.</para>
/// </summary>
public sealed record LatestMessageSummary(
    ConversationId ConversationId,
    string Body,
    DateTimeOffset CreatedAt,
    string? ContentKind = null,
    Guid? AttachmentId = null);
