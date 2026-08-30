using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.Categorization;

/// <summary>
/// `19-02`: the identical "no YandexGPT credentials configured" case
/// <see cref="ReplyDraft.UnconfiguredReplyDraftGenerator"/> establishes for `19-01`, applied to this
/// feature's own port. <c>ChatModule</c> registers this instead of
/// <see cref="ResilientConversationCategorizer"/> when
/// <see cref="Infrastructure.YandexGpt.CategorizationYandexGptOptions.ApiKey"/>/<c>FolderId</c> is
/// blank - <see cref="CategorizeConversationHandler"/>'s own caller (a periodic job, not an operator
/// waiting on a response) already treats <see cref="CategorizationResult.Unavailable"/> as "nothing to
/// do this tick," so an unconfigured deployment simply tags nothing, rather than failing to start.
/// </summary>
public sealed class UnconfiguredConversationCategorizer : IConversationCategorizer
{
    public Task<CategorizationResult> CategorizeAsync(
        CategorizationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<CategorizationResult>(
            new CategorizationResult.Unavailable("Automatic categorization is not configured for this deployment."));
}
