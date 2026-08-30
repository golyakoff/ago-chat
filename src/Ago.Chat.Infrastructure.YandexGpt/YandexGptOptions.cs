namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-01`: bound from `ReplyDraft:YandexGpt:*` - our own fixed application credentials, not a
/// per-tenant value, the identical shape `YooKassaOptions`'s own remarks describe for ЮKassa. Read
/// directly from `infra-credentials`/`docker/.env`, never written to Postgres, no cipher, no new
/// column - the same "every other external-API secret in this codebase already holds it this way" rule
/// `CLAUDE.md` states and `YooKassaOptions`/`TelegramBotApiOptions` already follow.
///
/// <para><see cref="ApiKey"/> authenticates every call (`Authorization: Api-Key {ApiKey}`, Yandex
/// Cloud's own documented scheme for a static service-account key - the closest fit to this codebase's
/// existing static-credential pattern, unlike the OAuth2 client-credentials + certificate exchange
/// Sber's GigaChat needs for the same job). <see cref="FolderId"/> scopes the call to the Yandex Cloud
/// folder the model quota and billing live under - required by the API, not a secret in the same sense
/// (it identifies an account, not a key), but validated alongside <see cref="ApiKey"/> below since a
/// missing value fails the call identically either way.</para>
/// </summary>
public sealed class YandexGptOptions
{
    public const string SectionName = "ReplyDraft:YandexGpt";

    /// <summary>Yandex Cloud's own documented Foundation Models completion endpoint - a public,
    /// well-known URL, not a secret, the same "hardcode the provider's real base URL, let options
    /// override it for a test's own fake host" shape `YooKassaOptions.BaseUrl`/`MaxBotApiOptions.BaseUrl`
    /// already establish.</summary>
    public string BaseUrl { get; set; } = "https://llm.api.cloud.yandex.net/foundationModels/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public string FolderId { get; set; } = string.Empty;

    /// <summary>Yandex Cloud's own model-URI shape is `gpt://{folderId}/{modelName}` -
    /// <c>yandexgpt-lite</c> is the smallest, cheapest model in the family, which is what a
    /// throwaway-if-wrong composer suggestion should cost against a real per-call price
    /// (`ReplyDraftOptions`'s own "context minimalism is also a cost lever" reasoning). Overridable per
    /// deployment without a code change if a larger model is ever warranted.</summary>
    public string ModelName { get; set; } = "yandexgpt-lite";

    /// <summary>Yandex Cloud's own upper bound on generated tokens for this call - kept small on
    /// purpose: a reply draft is a composer suggestion, not an essay, and every token generated is
    /// billed. String, not int, because that is the documented wire shape
    /// (`YandexGptCompletionOptions.MaxTokens`'s own remarks).</summary>
    public int MaxTokens { get; set; } = 300;
}
