namespace Ago.Chat.Domain;

public enum MessageAuthorKind
{
    Visitor,
    Operator,

    /// <summary>
    /// `14-04`: authored by AGO Chat itself, on behalf of the tenant - today the offline auto-reply
    /// (<see cref="Conversation.AddSystemMessage"/>) and nothing else.
    ///
    /// <para><b>This member is the offline auto-reply's loop guard, and that is its main job.</b> An
    /// automatic reply is an ordinary message, so it raises <see cref="MessageAdded"/> and reaches the
    /// same <c>MessageAccepted</c> topic the consumer that produced it is subscribed to. What stops
    /// that from recursing is not a counter, a flag on the conversation or a "was this generated"
    /// column - it is that the consumer acts on <see cref="Visitor"/> and refuses every other kind,
    /// and an auto-reply can never be a <see cref="Visitor"/> message because the only method that
    /// creates one hardcodes this value. Authoring it as <see cref="Operator"/> instead would have
    /// removed that guarantee <em>and</em> been a lie - there is no operator, which is the entire
    /// precondition for sending it.</para>
    ///
    /// <para>No database change: <c>messages.author_kind</c> is <c>text</c>, holding the enum member
    /// name, so a new member is additive by construction. Readers that predate it (the widget, the
    /// console) treat an unknown kind as "not mine" and render it on the incoming side, which is
    /// where a system message belongs anyway.</para>
    /// </summary>
    System,

    /// <summary>
    /// `23-64`/`adr/0148`: the widget's auto-open greeting, materialised as the conversation's real
    /// first message the moment the visitor writes (<see cref="Conversation.AddAutoGreetingMessage"/>)
    /// - never earlier, and never for a conversation that already has a message.
    ///
    /// <para><b>Not <see cref="Operator"/>.</b> No operator is assigned yet when this message is
    /// written (assignment is a separate, later step - <c>AssignConversationHandler</c>), so
    /// authoring it as <see cref="Operator"/> would fabricate a specific person's involvement that
    /// has not happened, and <see cref="Conversation.AddOperatorMessage"/>'s own participant/state
    /// invariants (a real, assigned <c>OperatorId</c>, <see cref="ConversationState.Assigned"/>)
    /// could not be satisfied honestly for it anyway.</para>
    ///
    /// <para><b>Not <see cref="System"/>, deliberately - this is the exception the backlog item
    /// itself asks to be recorded.</b> `23-56` (not yet built) will give <see cref="System"/> a
    /// tenant-editable machine name («Электронный помощник» or similar), precisely so a visitor is
    /// never told they are talking to a person who is not there. The author's own decision for this
    /// item is the opposite: the greeting reads as if from the shop's own side, not as a labelled
    /// machine reply. Reusing <see cref="System"/> here would make the greeting silently inherit
    /// `23-56`'s machine-name label the day that item ships - a regression nothing would catch, since
    /// it would look like `23-56` simply forgot this row. A distinct member is what keeps the
    /// exception visible in the type system rather than only in a code comment `23-56`'s own author
    /// might not read.</para>
    ///
    /// <para><b>Rendered like <see cref="Operator"/>, carrying <see cref="Conversation.SystemAuthorId"/>
    /// as its author id.</b> Today neither the widget nor the console resolve a per-message display
    /// name from <c>AuthorId</c> at all - both render a fixed, kind-based label
    /// (`ago-console`'s <c>threadAuthorOperator</c>/`ago-widget`'s <c>operatorLabel</c> for
    /// <see cref="Operator"/>, a distinct one for <see cref="System"/>). "The operator's own name" -
    /// the author's explicit decision for this item - is honoured by giving this member the identical
    /// visual treatment <see cref="Operator"/> already gets (same label, same side of the transcript),
    /// while staying honest at the data level: <see cref="Guid.Empty"/>, the same sentinel
    /// <see cref="System"/> already uses, because there is no real principal behind it and inventing
    /// one - a fabricated <c>OperatorId</c> - would be a lie the next reader of this column could not
    /// detect.</para>
    /// </summary>
    AutoGreeting,
}
