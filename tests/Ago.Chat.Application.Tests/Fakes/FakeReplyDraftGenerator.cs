using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Captures the exact <see cref="ReplyDraftGenerationRequest"/> it was called with, so a test
/// can inspect what `GenerateReplyDraftHandler` actually sent - the Application-level half of `19-01`'s
/// own "no more context than this conversation's own history" proof (the Infrastructure-level half is
/// `YandexGptReplyDraftClientTests`' own real outbound HTTP body).</summary>
public sealed class FakeReplyDraftGenerator : IReplyDraftGenerator
{
    public ReplyDraftGenerationRequest? LastRequest { get; private set; }

    public ReplyDraftGenerationResult NextResult { get; set; } = new ReplyDraftGenerationResult.Success("a suggested reply");

    public Task<ReplyDraftGenerationResult> GenerateDraftAsync(
        ReplyDraftGenerationRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(NextResult);
    }
}
