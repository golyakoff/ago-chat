namespace Ago.Chat.Infrastructure.Telegram;

/// <summary>
/// `14-07`/`adr/0070` (in <c>ago-root</c>): the outbound SOCKS5 relay this deployment's own outbound
/// calls to Telegram need - measured, on this VPS, to be unreachable directly about half the time; a
/// SOCKS5 proxy backed by a relay fixed it completely. Nothing else in this codebase proxies its
/// outbound traffic (not MAX, not the S3 client, not Keycloak's client) - this is genuinely new wiring,
/// registered where <c>TelegramApiClient</c>'s <see cref="System.Net.Http.HttpClient"/> is built
/// (<c>ChatModule.ConfigureServices</c>), not inside <c>TelegramApiClient</c> itself. That placement is
/// deliberate: <c>TelegramApiClient</c>'s whole point (mirroring <c>MaxApiClient</c>'s own remarks) is to
/// stay a thin, provider-shape-only HTTP client with no opinion on how its traffic reaches the network;
/// baking proxy-awareness into the client itself would make it responsible for this deployment's own
/// network topology too, a decision that belongs in the host's composition root
/// (clean-architecture.md), not in a class that has to stay meaningful in any deployment, including one
/// with no relay at all.
///
/// <para><b>This is deployment configuration, not a tenant secret.</b> Unlike a bot token
/// (<c>Domain.ChannelCredential</c>, one row per site, encrypted at rest), every tenant on this
/// deployment shares the same outbound relay - there is exactly one proxy for the whole process, not
/// one per site. It belongs in ordinary configuration (an env var), never in the credential store, and
/// never as a real value committed to this repository (CLAUDE.md: "everything is public").</para>
///
/// <para><see cref="Socks5Address"/> defaults to <see langword="null"/> - proxying off, a direct
/// connection - which is what a deployment with no configured relay (this item's own local/dev loop,
/// which has no relay to reach) must fall back to safely. Bound from
/// <c>Channels:Telegram:Proxy:Socks5Address</c>, i.e. the environment variable
/// <c>Channels__Telegram__Proxy__Socks5Address</c>.</para>
/// </summary>
public sealed class TelegramProxyOptions
{
    public const string SectionName = "Channels:Telegram:Proxy";

    /// <summary>The relay's own SOCKS5 listener, as <c>host:port</c> (no scheme - the caller builds the
    /// <c>socks5://</c> URI itself; see <c>ChatModule</c>'s own registration). <see langword="null"/>
    /// or unset means "proxy off, connect directly" - the only supported non-proxied path, not a
    /// production default.</summary>
    public string? Socks5Address { get; init; }
}
