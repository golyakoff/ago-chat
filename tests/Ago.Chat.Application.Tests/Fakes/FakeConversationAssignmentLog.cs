using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `23-03`: in-memory <see cref="IConversationAssignmentLog"/> - real atomicity (the interval landing
/// in the same transaction as the conversation's own save) is a claim about Postgres, proven in
/// <c>Ago.Chat.Integration.Tests</c>/<c>Ago.Chat.Concurrency.Tests</c> exactly like
/// <see cref="FakeUnitOfWork"/>'s own remarks describe for itself. What a handler unit test can prove
/// is the decision: whether, and with what <see cref="ConversationAssignmentSource"/>, a handler opened
/// or closed an interval for a given attempt.
/// </summary>
public sealed class FakeConversationAssignmentLog : IConversationAssignmentLog
{
    public List<ConversationAssignmentInterval> Opened { get; } = [];

    public List<ConversationId> ClosedFor { get; } = [];

    public void Open(ConversationAssignmentInterval interval) => Opened.Add(interval);

    public Task CloseOpenAsync(ConversationId conversationId, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        ClosedFor.Add(conversationId);
        var open = Opened.LastOrDefault(i => i.ConversationId == conversationId && i.EndedAt is null);
        open?.Close(endedAt);
        return Task.CompletedTask;
    }
}
