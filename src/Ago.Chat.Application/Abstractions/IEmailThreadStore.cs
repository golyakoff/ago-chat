using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-09`: the write-side port for <see cref="EmailThreadState"/> - Postgres is exactly the kind of
/// external resource CLAUDE.md rule 2 requires a port for, and this interface is the seam that lets
/// <c>Ago.Chat.Api</c>'s <c>EmailWebhookEndpoints</c> and <c>Ago.Chat.Infrastructure.Email</c>'s
/// <c>EmailChannelAdapter</c> each read/write it without either one referencing
/// <c>Ago.Chat.Infrastructure.Postgres</c> or EF Core directly - the alternative, injecting
/// <c>AgoChatDbContext</c> into either, would make both untestable without a real database and would put
/// an Infrastructure-layer type in a project the dependency rule forbids it from reaching (Application and
/// a sibling Infrastructure project must not know about each other's own concrete shape).
///
/// <para>Named a "store", not a "repository", to match this codebase's own existing split -
/// <see cref="ISiteRepository"/>/<see cref="IChannelCredentialRepository"/> front an aggregate with a real
/// lifecycle (register, revoke); this fronts a simpler, always-upserted fact with no state machine of its
/// own. Nothing hinges on the name; it is chosen for a reader's sake, not enforced by a test.</para>
/// </summary>
public interface IEmailThreadStore
{
    /// <summary>The thread state for one conversation, or <see langword="null"/> if no inbound email has
    /// been recorded for it yet - what <c>EmailChannelAdapter.SendAsync</c> reads to build
    /// <c>In-Reply-To</c>/<c>References</c>, and what <c>EmailWebhookEndpoints</c> reads to decide whether
    /// an inbound delivery should call <see cref="EmailThreadState.Start"/> or
    /// <see cref="EmailThreadState.RecordInbound"/>.</summary>
    Task<EmailThreadState?> GetAsync(ConversationId conversationId, CancellationToken cancellationToken);

    /// <summary><see cref="IChannelCredentialRepository.SaveAsync"/>'s own detached-means-insert shape:
    /// a state EF has never tracked is inserted, one it loaded via <see cref="GetAsync"/> and then mutated
    /// is updated.</summary>
    Task SaveAsync(EmailThreadState state, CancellationToken cancellationToken);
}
