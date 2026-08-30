using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Module.ReplyDraft;

/// <summary>
/// `19-01`: the "no YandexGPT credentials configured for this deployment" case, distinguished from a
/// reachable-but-broken provider (<see cref="ResilientReplyDraftGenerator"/>'s own job) only in that no
/// HTTP call is ever attempted - both degrade to the identical
/// <see cref="ReplyDraftGenerationResult.Unavailable"/> outcome, because a caller has no way to act on
/// "misconfigured" differently than "temporarily down": either way, no suggestion is available right
/// now (`resilience.md`'s degrade-to-"suggestion unavailable" rule, restated here for a configuration
/// gap rather than a network fault).
///
/// <para><b>Why this exists instead of `ValidateOnStart()` refusing to boot.</b> No real YandexGPT
/// account exists in every environment this project runs in yet (`19-01`'s own backlog file states this
/// honestly), and a per-deployment optional feature should not be able to take an entire host down at
/// startup for every other, unrelated capability that host serves. <c>ChatModule</c> checks
/// <see cref="Infrastructure.YandexGpt.YandexGptOptions.ApiKey"/>/<c>FolderId</c> at composition time
/// and registers this class instead of <see cref="ResilientReplyDraftGenerator"/> when either is blank -
/// the same "degrade the one feature, not the process" choice `18-14`'s own honest-limits framing makes
/// for a different reason.</para>
/// </summary>
public sealed class UnconfiguredReplyDraftGenerator : IReplyDraftGenerator
{
    public Task<ReplyDraftGenerationResult> GenerateDraftAsync(
        ReplyDraftGenerationRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<ReplyDraftGenerationResult>(
            new ReplyDraftGenerationResult.Unavailable("Reply-draft assist is not configured for this deployment."));
}
