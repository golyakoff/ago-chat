namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-01`: the terminal half of the terminal/transient split every other outbound third-party client
/// in this codebase already makes (`TelegramApiClient`, `YooKassaPaymentsApiClient`'s own remarks) - a
/// 4xx from YandexGPT (a malformed request, an expired/revoked API key, a folder the key cannot use)
/// that retrying the identical request would not fix. Thrown rather than returned as a value, unlike
/// `CreatePaymentResult.Refused`: this port's own contract
/// (<see cref="Ago.Chat.Application.Abstractions.ReplyDraftGenerationResult"/>) has no terminal case for
/// a caller to act on differently - every failure this feature can have degrades to the identical
/// "suggestion unavailable", so the only reason this is its own exception type rather than a bare
/// <see cref="HttpRequestException"/> is so <see cref="Ago.Chat.Module.ReplyDraft.ReplyDraftResiliencePipeline"/>
/// can exclude it from retry - retrying a permanently-broken credential three times would waste real
/// money against a real per-call budget for an outcome no retry could ever change.
/// </summary>
public sealed class ReplyDraftProviderRefusedException(string reason) : Exception(reason);
