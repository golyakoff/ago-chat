namespace Ago.Chat.Domain;

/// <summary>
/// `23-69`/`23-77`: which of the two triggers wrote one <c>visitor_restrictions</c> row - not a
/// severity ranking, a provenance tag. Both kinds are enforced by the identical check
/// (<c>IVisitorRestrictionRepository.IsActiveAsync</c>, read by <c>StartConversationHandler</c>); the
/// only difference between them is what <c>ExpiresAt</c> holds and who is allowed to lift one early
/// (<c>Permission.ConversationMarkSpam</c> for <see cref="Spam"/>, <c>Permission.ConversationBlock</c>
/// for <see cref="Block"/> - <c>LiftVisitorRestrictionHandler</c>'s own remarks).
/// </summary>
public enum VisitorRestrictionKind
{
    /// <summary>`23-69`: written by <c>CloseConversationAsSpamHandler</c> alongside an ordinary close -
    /// always carries a real <c>ExpiresAt</c> ("a window of time", this item's own answered Question
    /// 1), never <see langword="null"/>.</summary>
    Spam,

    /// <summary>`23-77`: written by <c>BlockVisitorHandler</c>, independent of closing - always carries
    /// <c>ExpiresAt = null</c> (indefinite, "reversible... exactly as `24-10` already does" - this
    /// item's own Scope), lifted only by a deliberate act.</summary>
    Block,
}
