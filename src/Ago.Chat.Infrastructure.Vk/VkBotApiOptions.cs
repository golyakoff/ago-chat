namespace Ago.Chat.Infrastructure.Vk;

/// <summary>
/// `14-08`: confirmed against VK's own official SDK (github.com/VKCOM/vk-php-sdk), 2026-08-29 - base
/// URL <c>https://api.vk.com/method</c>, every call versioned with a <c>v</c> parameter (that SDK
/// currently targets <c>5.199</c>; this item pins the same value rather than inventing its own, since
/// nothing about this item's own calls needs a newer or older behaviour), the access token travels as
/// an ordinary <c>access_token</c> POST parameter, not a header or a URL-path segment the way MAX's and
/// Telegram's own tokens do (<c>MaxBotApiOptions</c>/<c>TelegramBotApiOptions</c>' own remarks on their
/// respective shapes).
///
/// <para><b><see cref="PublicWebhookBaseUrl"/> is not optional the way <c>MaxBotApiOptions</c>'s own
/// field of the same name is.</b> MAX ships both a webhook receiver and a long-polling loop, so an
/// unconfigured deployment still works locally. VK's Callback API has no comparable "development mode"
/// this item builds against: the only way VK ever delivers an event is a real HTTP callback to a real
/// public URL VK itself can reach, and there is no VK-side long-poll equivalent this item implements
/// (see <c>VkChannelAdapter</c>'s own remarks for why building one was scoped out). So this field is
/// left nullable at the options level only so a deployment that has not configured
/// <c>Channels:Vk</c> at all does not fail to start - every other host on this codebase does that too
/// - but <c>VkChannelEndpoints</c> refuses to let an operator connect VK at all while it is unset
/// (<c>ConversationErrors.ChannelNotAvailable</c>), because there would be nothing this system could
/// hand VK to call back to.</para>
/// </summary>
public sealed class VkBotApiOptions
{
    public const string SectionName = "Channels:Vk";

    public string BaseUrl { get; init; } = "https://api.vk.com/method";

    public string ApiVersion { get; init; } = "5.199";

    public Uri? PublicWebhookBaseUrl { get; init; }
}
