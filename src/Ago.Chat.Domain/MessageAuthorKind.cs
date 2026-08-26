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
}
