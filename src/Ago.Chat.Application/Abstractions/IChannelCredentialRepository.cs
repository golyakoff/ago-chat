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

    /// <summary>
    /// `14-10`: the active credential whose <see cref="ChannelCredential.ProviderAccountId"/> matches, for
    /// one channel - what <c>WhatsAppWebhookEndpoints</c> resolves a tenant by, because unlike every other
    /// channel's inbound webhook, WhatsApp's own callback URL carries no per-tenant path segment at all
    /// (Meta's own "tech provider" model puts every onboarded client behind one App-wide webhook -
    /// <c>WhatsAppBotApiOptions</c>' own remarks). No other adapter needs this: MAX/Telegram/VK each
    /// resolve their credential from a path segment (<see cref="GetByIdAsync"/>) or, for a poller, iterate
    /// every active row (<see cref="GetAllActiveAsync"/>) - this is the first inbound path that has to go
    /// the other way, from a provider-owned identifier in the payload back to the tenant that owns it.
    ///
    /// <para>Returns only an <em>active</em> match, unlike <see cref="GetByIdAsync"/> - deliberately.
    /// <see cref="GetByIdAsync"/> must still find a revoked row so a webhook receiver can answer "this
    /// channel was disconnected" rather than "this URL was never registered" (`adr/0069`'s own note); this
    /// lookup has no per-credential URL to distinguish those two cases on, so a revoked credential's own
    /// <c>phone_number_id</c> is treated identically to one that never existed - a delivery this system can
    /// no longer attribute to anyone is acknowledged and dropped either way (<c>WhatsAppWebhookEndpoints</c>'
    /// own remarks).</para>
    ///
    /// <para>Relies on the provider's own guarantee that a <c>phone_number_id</c> is globally unique -
    /// nothing in this schema enforces that a second tenant could never register the identical value (though
    /// <c>ChannelCredentialConfiguration</c>'s own <c>ux_channel_credentials_kind_provideraccountid_active</c>
    /// index refuses to let this system itself create that collision), so this returns the first match rather
    /// than failing loudly on more than one.</para>
    /// </summary>
    Task<ChannelCredential?> GetActiveByProviderAccountIdAsync(
        ChannelKind kind, string providerAccountId, CancellationToken cancellationToken);

    Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken);
}
