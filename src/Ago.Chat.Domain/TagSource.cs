namespace Ago.Chat.Domain;

/// <summary>
/// `19-02`: who applied a given <see cref="Tag"/> to a given <see cref="Conversation"/> - the one field
/// the <c>conversation_tags</c> join row gains for this item, on top of the pair of ids `18-04` already
/// established (`ConversationTagRecord`'s own remarks). Exists because the item's own Done-when is
/// explicit that an AI-applied tag must be "visibly distinguishable from an operator-applied one... at
/// the data level (a source column, or a distinct flag)" - a real trust-and-correction signal an
/// operator needs to tell "the AI guessed this" from "a colleague decided this", not a detail worth
/// collapsing away.
///
/// <para>A closed, two-value vocabulary rather than a free-text or nullable "applied by" field - the
/// same "small, deliberately closed enum" shape <see cref="ConversationOutcome"/> already establishes
/// for the identical reason: this value is aggregated and filtered on (the console's own "AI tagged"
/// badge, a future report), never displayed as prose, so there is nothing here for free text to buy.
/// </para>
/// </summary>
public enum TagSource
{
    /// <summary>The default and the only value <c>18-04</c>'s own <see cref="TagConversationHandler"/>
    /// ever writes - an operator's own explicit action through the existing tagging UI, unchanged by
    /// this item.</summary>
    Operator,

    /// <summary>Written by exactly one caller: `Ago.Chat.Application.UseCases.CategorizeConversation.CategorizeConversationHandler`,
    /// itself reached only from `Ago.Chat.Worker.ConversationCategorizationJob` - never from a
    /// request an operator or a visitor triggers directly (`adr/0078`'s kind 2, "a periodic batch job,
    /// not real-time classification").</summary>
    Ai,
}
