using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `5-05`: shaped around the one thing that needs it - resolving a validated OIDC principal back to
/// an operator (`adr/0022`) - not a general operator CRUD port. Grow this only when a second real
/// caller needs a different question answered.
/// </summary>
public interface IOperatorRepository
{
    Task<Operator?> GetByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken);

    /// <summary>
    /// `14-04`: is *anybody* on duty for this site right now - the one question the offline
    /// auto-reply's own precondition asks, and nothing wider. Shaped around the caller exactly the way
    /// <c>ISiteRepository.AnyAllowsOriginAsync</c> already is: returning the operators instead would
    /// hand the caller a list it has no use for and invite a second, different capacity judgment to
    /// grow next to the assignment engine's.
    ///
    /// <para><b>Deliberately weaker than the assignment engine's own candidate query.</b>
    /// <c>SkipLockedAssignmentClaimer</c> looks for an <c>Online</c> operator <em>with room</em>
    /// (<c>active_chats &lt; capacity</c>); this asks only whether one is <c>Online</c>. The
    /// difference is the whole point: an online operator who is momentarily full is a human being who
    /// will get to this conversation, and telling that visitor "nobody is here" would be false and
    /// would land seconds before a real answer. "Every operator is busy" is a queue-wait, not an
    /// absence.</para>
    ///
    /// <para>Read from Postgres, never from Redis presence: this decides whether to write a message,
    /// so it is a write decision, and <c>CLAUDE.md</c> rule 8 puts it in the database. The
    /// <c>operators.status</c> column is also the same input the assignment engine reads, so the two
    /// cannot disagree about who is on duty.</para>
    /// </summary>
    Task<bool> AnyOnlineForSiteAsync(SiteId siteId, CancellationToken cancellationToken);
}
