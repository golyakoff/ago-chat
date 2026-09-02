using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// One held claim on a channel credential's poll loop (`adr/0089`). Session-scoped: there is no TTL,
/// no renewal and no heartbeat to get wrong - the underlying database session's own liveness is the
/// only thing that defines ownership. Disposing releases the claim explicitly and promptly, which is
/// what makes takeover on a clean shutdown fast rather than merely eventual; an undisposed lease is
/// still released, because the session ending - not this object's lifetime - is the real mechanism.
/// </summary>
public interface IChannelPollerLease : IAsyncDisposable
{
    ChannelCredentialId CredentialId { get; }

    /// <summary>
    /// Confirms this lease is still backed by a live session. `adr/0089` names a half-open-connection
    /// window as the one case a process can wrongly believe it still owns a lease PostgreSQL has
    /// already released (a black-holed TCP session, not a clean close) - this is the guard against it,
    /// meant to be called once per poll iteration rather than trusting an acquired lease indefinitely.
    /// A broken connection throws <see cref="ChannelPollerLeaseLostException"/>; the caller has lost
    /// every lease this process holds, not only this one, because `adr/0089`'s chosen adapter keeps
    /// exactly one connection per Worker process, not one per credential.
    /// </summary>
    Task VerifyStillHeldAsync(CancellationToken cancellationToken);
}
