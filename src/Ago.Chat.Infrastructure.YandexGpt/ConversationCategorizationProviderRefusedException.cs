namespace Ago.Chat.Infrastructure.YandexGpt;

/// <summary>
/// `19-02`: the terminal half of the terminal/transient split, the identical shape
/// <see cref="ReplyDraftProviderRefusedException"/>'s own remarks give for `19-01` - a 4xx from
/// YandexGPT that retrying the identical request would not fix. Thrown rather than returned as a value,
/// for the identical reason: this port's own contract
/// (<see cref="Ago.Chat.Application.Abstractions.CategorizationResult"/>) has no terminal case for a
/// caller to act on differently - every failure degrades to the same "no categorization this cycle",
/// which is what makes this its own exception type (so
/// <see cref="Ago.Chat.Module.Categorization.CategorizationResiliencePipeline"/> can exclude it from
/// retry) rather than a bare <see cref="HttpRequestException"/>.
/// </summary>
public sealed class ConversationCategorizationProviderRefusedException(string reason) : Exception(reason);
