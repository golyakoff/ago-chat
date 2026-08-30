using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Captures the exact <see cref="CategorizationRequest"/> it was called with, so a test can
/// inspect what `CategorizeConversationHandler` actually sent - the identical role
/// <see cref="FakeReplyDraftGenerator"/> plays for `19-01`.</summary>
public sealed class FakeConversationCategorizer : IConversationCategorizer
{
    public CategorizationRequest? LastRequest { get; private set; }

    public CategorizationResult NextResult { get; set; } = new CategorizationResult.Success([]);

    public Task<CategorizationResult> CategorizeAsync(CategorizationRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(NextResult);
    }
}
