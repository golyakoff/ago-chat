using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-32`: the write half of a tenant's team chat - one implicit-transaction port, the same shape
/// <see cref="IModuleQuantityGrantStore"/> already takes rather than a repository plus a separate
/// outbox call (that interface's own remarks): the state change and the
/// <c>Ago.Chat.Contracts.TeamMessagePosted</c> event it describes commit or roll back together
/// (CLAUDE.md rule 4) inside <see cref="PostAsync"/> itself, with no second call for a caller to
/// remember.
///
/// <para>Takes an already-validated <see cref="MessageBody"/>, an already-decided
/// <paramref name="authorIsAdmin"/>, and an already-minted <paramref name="id"/>/
/// <paramref name="now"/> - the same "Application decides the policy, Infrastructure only persists
/// it" split <c>SendOperatorMessageHandler</c> draws for its own RBAC check before ever reaching a
/// port. <c>SendTeamMessageHandler</c> is the one caller, and it is the one place that knows *why*
/// the author is or is not labelled the tenant's admin - this port has no opinion on that question at
/// all.</para>
/// </summary>
public interface ITeamChatRepository
{
    /// <summary>
    /// Assigns the next per-site sequence (a database compare-and-set, CLAUDE.md rule 8 - see
    /// <c>Ago.Chat.Domain.TeamMessage</c>'s own remarks for why there is no in-memory aggregate to do
    /// this instead), persists the message, and stages the outbox row that drives realtime fan-out -
    /// all inside the one <c>SaveChangesAsync</c> this call makes.
    ///
    /// <para><paramref name="clientMessageId"/> retry-dedup: a second call carrying a
    /// <paramref name="clientMessageId"/> already used for this <paramref name="siteId"/> returns the
    /// message that first call produced rather than posting a second one - safe for a caller to retry
    /// after a <c>SendOutcomeUnknownError</c>-shaped failure, the same guarantee `5-07` gives every
    /// other send path in this product.</para>
    /// </summary>
    Task<TeamMessage> PostAsync(
        SiteId siteId,
        OperatorId authorId,
        bool authorIsAdmin,
        MessageBody body,
        Guid? clientMessageId,
        TeamMessageId id,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
