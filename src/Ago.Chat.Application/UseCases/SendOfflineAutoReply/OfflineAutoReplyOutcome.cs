namespace Ago.Chat.Application.UseCases.SendOfflineAutoReply;

/// <summary>
/// `14-04`: why <see cref="SendOfflineAutoReplyHandler"/> did or did not reply. Every value except
/// <see cref="Sent"/> is a *success* - "correctly decided not to answer" is the common case, not an
/// error, and a consumer must ack it rather than retry it.
///
/// <para>Enumerated rather than collapsed into a <c>bool</c> because the reasons are genuinely
/// different and one of them is the loop guard: a test asserting "no reply was sent" proves almost
/// nothing, while a test asserting <see cref="NotAVisitorMessage"/> proves *which* rule stopped
/// it.</para>
/// </summary>
public enum OfflineAutoReplyOutcome
{
    /// <summary>A scripted reply was written and outboxed.</summary>
    Sent,

    /// <summary><b>The loop guard fired.</b> The triggering message was not authored by the visitor -
    /// most importantly, it was an auto-reply of this system's own
    /// (<c>MessageAuthorKind.System</c>). See <see cref="SendOfflineAutoReplyHandler"/>.</summary>
    NotAVisitorMessage,

    /// <summary>The site has offline auto-reply switched off - the default for every tenant.</summary>
    Disabled,

    /// <summary>The conversation is not waiting for anybody: it is already assigned to an operator, or
    /// closed. A reply here would talk over a human.</summary>
    ConversationNotWaiting,

    /// <summary>At least one operator is <c>Online</c> for this site. They may all be at capacity -
    /// that is a queue wait, not an absence, and this system does not tell a visitor nobody is here
    /// when somebody is.</summary>
    OperatorOnline,

    /// <summary>Enabled, nobody on duty, but the script has nothing to say to this message - no rule
    /// matched and no fallback text is configured.</summary>
    NothingToSay,

    /// <summary>This exact <c>MessageAccepted</c> was already processed by this consumer: the inbox
    /// ledger rejected the second attempt and nothing, including the reply that had been staged, was
    /// persisted (`adr/0017`).</summary>
    AlreadyReplied,
}
