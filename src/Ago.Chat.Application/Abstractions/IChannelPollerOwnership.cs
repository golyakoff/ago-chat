using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-16`/`adr/0089`: exclusive ownership of one channel's poll loop, claimed per
/// <see cref="ChannelCredentialId"/>. A Worker process polls a credential if and only if it holds the
/// <see cref="IChannelPollerLease"/> this returns - see that interface's own remarks for what "holds"
/// means once granted, and <c>adr/0089</c> for the full mechanism (a session-scoped PostgreSQL advisory
/// lock) and why it was chosen over a Redis lease or a leased row.
///
/// <para>The port lives here, in <c>Ago.Chat.Application.Abstractions</c>, because CLAUDE.md rule 2
/// ("every external resource sits behind a port") and the dependency rule forbid Application - and the
/// two poller classes in <c>Ago.Chat.Infrastructure.Telegram</c>/<c>Ago.Chat.Infrastructure.MaxBot</c>
/// that consume this port, since neither may reference Npgsql - from knowing this is backed by
/// PostgreSQL at all. The alternative, injecting <c>NpgsqlDataSource</c> straight into the poller
/// classes, would make ownership untestable without a real database from those classes' own unit-test
/// surface, and would leak a storage decision (which adr/0089 explicitly does not want reachable by
/// other channel adapters or promoted to `Ago.Platform.*`) into two classes whose only job is to speak
/// one provider's own long-poll protocol.</para>
/// </summary>
public interface IChannelPollerOwnership
{
    /// <summary>
    /// Attempts to claim exclusive ownership of <paramref name="credentialId"/>'s poll loop. Returns
    /// <see langword="null"/> immediately - never blocks - if another process already holds it.
    /// <c>adr/0089</c>'s decision is that a process which cannot acquire simply does not poll that
    /// credential and retries on its own next refresh tick; there is no queueing and no back-pressure
    /// signal beyond that.
    /// </summary>
    Task<IChannelPollerLease?> TryAcquireAsync(ChannelCredentialId credentialId, CancellationToken cancellationToken);
}
