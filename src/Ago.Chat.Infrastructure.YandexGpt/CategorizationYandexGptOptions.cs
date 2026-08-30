namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-02`: bound from `ConversationCategorization:YandexGpt:*` - our own fixed application credentials,
/// not a per-tenant value, the identical shape <see cref="YandexGptOptions"/>'s own remarks describe for
/// `19-01`.
///
/// <para><b>Its own class, not a second binding of <see cref="YandexGptOptions"/> under a different
/// section.</b> Considered and rejected: <c>IOptions&lt;YandexGptOptions&gt;</c> is resolved once per
/// type by the DI container, so making two features share it would need named-options machinery
/// (<c>IOptionsFactory</c>/<c>IOptionsMonitor.Get(name)</c>) neither `19-01` nor this item otherwise
/// needs, purely to save duplicating five properties. The duplication also buys something real: `19-01`'s
/// `ApiKey`/`FolderId` and this item's own can be rotated independently
/// (`docs/architecture/secrets.md`'s own rotation-cost classes), and a change to one feature's model or
/// token budget cannot accidentally retune the other's.</para>
/// </summary>
public sealed class CategorizationYandexGptOptions
{
    public const string SectionName = "ConversationCategorization:YandexGpt";

    /// <summary>Same public, well-known Foundation Models endpoint <see cref="YandexGptOptions.BaseUrl"/>
    /// defaults to - not a secret, overridable per deployment.</summary>
    public string BaseUrl { get; set; } = "https://llm.api.cloud.yandex.net/foundationModels/v1/";

    public string ApiKey { get; set; } = string.Empty;

    public string FolderId { get; set; } = string.Empty;

    /// <summary>`yandexgpt-lite`, the same smallest/cheapest family member `19-01` defaults to - a
    /// closed-vocabulary classification call needs less reasoning depth than a free-text reply draft,
    /// not more.</summary>
    public string ModelName { get; set; } = "yandexgpt-lite";

    /// <summary>Smaller than <see cref="YandexGptOptions.MaxTokens"/>'s own default: the only valid
    /// response shape is a short JSON array of tag names from a small, bounded site vocabulary, never
    /// prose - <see cref="YandexGptConversationCategorizerClient"/>'s own remarks on the response
    /// contract.</summary>
    public int MaxTokens { get; set; } = 200;
}
