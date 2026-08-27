using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-02`: the write-side port for the <see cref="ChannelCredential"/> aggregate - <see
/// cref="IWebhookEndpointRepository"/>'s own shape, adapted to a (site, channel) key instead of a
/// generated id being the natural lookup for most callers.
/// </summary>
public interface IChannelCredentialRepository
{
    /// <summary>The active credential for this (site, channel) pair, or <see langword="null"/> - what
    /// <c>RegisterChannelCredentialHandler</c> checks before registering a new one (`adr/0069`'s "one
    /// bot per tenant per channel"), and what the outbound send path
    /// (<c>Ago.Chat.Infrastructure.MaxBot.MaxChannelAdapter</c>) resolves per message.</summary>
    Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken);

    /// <summary>Every active credential for one channel, across every tenant - what a polling loop
    /// (`Ago.Chat.Infrastructure.MaxBot.MaxLongPollingService`, the dev-only mechanism MAX's own
    /// documentation calls suitable for local testing before a webhook can be registered) needs to know
    /// which bots to poll. A webhook receiver never calls this: it is handed exactly one credential id
    /// by its own URL path and resolves it with <see cref="GetByIdAsync"/> instead.</summary>
    Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken);

    /// <summary>The credential this inbound webhook's path segment names, active or not - a revoked
    /// credential must still be found so the webhook receiver can answer "this channel was
    /// disconnected" rather than "this URL was never registered" (`adr/0069`'s revocation-is-a-real-
    /// state note).</summary>
    Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken);

    Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken);
}
