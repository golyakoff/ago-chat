namespace Ago.Chat.Infrastructure.Avito;

/// <summary>
/// `14-11`: two genuinely different kinds of configuration in one options group, the way
/// <see cref="YooKassaOptions"/> (a different provider, same shape) already establishes for this
/// codebase - a public, well-known base URL versus a real secret pair, bound from the same section
/// because both are this deployment's own fixed values, never a tenant's.
///
/// <para><b><see cref="ClientId"/>/<see cref="ClientSecret"/> are AGO's own Avito API application
/// credentials, not a shop's</b> - the one genuinely new kind of secret this channel introduces. Every
/// other channel this project has built (MAX/Telegram/VK/WhatsApp) treats "the app" and "the tenant's
/// own bot/community/number" as the identical thing: a shop registers its own bot with MAX, its own
/// community with VK, and hands AGO that token directly. Avito's Messenger API is different: a shop
/// authorizes <em>AGO's own registered Avito integration</em> via OAuth 2 (`authorization_code`,
/// confirmed from Avito's own published OpenAPI schema - <c>AvitoDtos.cs</c>'s own citation), which
/// means AGO must first exist as an Avito API application at all, with its own <c>client_id</c>/
/// <c>client_secret</c> - a value shared across every tenant, closer in shape to
/// <see cref="YooKassaOptions.ShopId"/>/<see cref="YooKassaOptions.SecretKey"/> (AGO's own fixed
/// application credentials) than to anything a per-tenant <c>ChannelCredential</c> row holds. Used only
/// by <see cref="AvitoApiClient.RefreshAccessTokenAsync"/> - Avito's own <c>/token</c>
/// <c>grant_type=refresh_token</c> call requires the calling application's own identity alongside the
/// tenant's refresh token (Avito's own <c>RefreshRequest</c> schema, <c>AvitoDtos.cs</c>).</para>
///
/// <para><b>No Avito API application exists in the environment this item was built in</b> - the same
/// honest gap this item's own report names for the live-verification Done-when box. Registering AGO as
/// an Avito developer/integrator (obtaining a real <see cref="ClientId"/>/<see cref="ClientSecret"/>
/// pair) is a prerequisite this item did not and could not complete, distinct from (and prior to) any
/// individual shop completing Avito's own OAuth consent flow.</para>
/// </summary>
public sealed class AvitoApiOptions
{
    public const string SectionName = "Channels:Avito";

    /// <summary>Avito's own documented Messenger API base - a public, well-known URL, not a secret, the
    /// same real-default shape <c>MaxBotApiOptions.BaseUrl</c>/<c>VkBotApiOptions.BaseUrl</c> already
    /// establish.</summary>
    public string BaseUrl { get; init; } = "https://api.avito.ru";

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    /// <summary><see cref="VkBotApiOptions.PublicWebhookBaseUrl"/>'s own shape and own reasoning: Avito's
    /// Messenger API has no development-mode fallback this item builds against (no polling alternative -
    /// <c>AvitoChannelAdapter</c>'s own remarks), so a deployment that has not configured
    /// <c>Channels:Avito</c> at all still starts, but <c>AvitoChannelEndpoints</c> refuses to let an
    /// operator connect Avito while this is unset.</summary>
    public Uri? PublicWebhookBaseUrl { get; init; }
}
