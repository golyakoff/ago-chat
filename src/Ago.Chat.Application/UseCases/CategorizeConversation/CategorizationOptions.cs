namespace Ago.Chat.Application.UseCases.CategorizeConversation;

/// <summary>
/// Bound from `ConversationCategorization:*` config keys - the identical one non-secret, non-batching
/// knob <see cref="Application.UseCases.GenerateReplyDraft.ReplyDraftOptions"/> carries for the reply-
/// draft feature, for the same "context minimalism is also a cost lever" reason
/// (<see cref="Application.UseCases.GenerateReplyDraft.ReplyDraftOptions"/>'s own remarks): how much of
/// a conversation's own history a categorization prompt gets to see.
///
/// <para>Its own options class rather than reusing <see cref="Application.UseCases.GenerateReplyDraft.ReplyDraftOptions"/>
/// - the two features have unrelated tuning needs (a batch classifier over a closed conversation's whole
/// arc is not the same shape as a live composer suggestion over the last few turns) and no shared
/// caller, the same "distinct knob per distinct use case" reasoning this item's own
/// <see cref="Abstractions.IConversationCategorizer"/> already gives for not extending
/// <see cref="Abstractions.IReplyDraftGenerator"/>.</para>
///
/// Not measured or load-tested - the same caveat every other options class in this codebase carries
/// for its own defaults.
/// </summary>
public sealed class CategorizationOptions
{
    public const string SectionName = "ConversationCategorization";

    /// <summary>How many of the conversation's most recent messages to include, oldest first once
    /// re-ordered. Larger than <see cref="Application.UseCases.GenerateReplyDraft.ReplyDraftOptions.HistoryMessageCount"/>'s
    /// own default: a reply draft only needs the shape of the last exchange, but classifying a whole
    /// conversation's topic benefits from seeing more of how it opened and where it ended.</summary>
    public int HistoryMessageCount { get; set; } = 40;
}
