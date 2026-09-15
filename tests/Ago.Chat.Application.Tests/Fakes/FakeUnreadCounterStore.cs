using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// Records every call rather than mutating a <see cref="Conversation"/> - unlike the pre-`25-109` fake
/// shape, there is no aggregate for this port to touch at all (<see cref="IUnreadCounterStore"/>'s own
/// remarks: the whole point is a write that never loads one). A handler test asserts against
/// <see cref="Calls"/> instead of a conversation's own counters; only a real-Postgres test
/// (<c>Ago.Chat.Integration.Tests</c>) can prove the SQL itself is correct and atomic with the
/// inbox-dedup row.
/// </summary>
public sealed class FakeUnreadCounterStore : IUnreadCounterStore
{
    public List<(ConversationId ConversationId, MessageAuthorKind AuthorKind, int Sequence)> Calls { get; } = [];

    public Task IncrementAsync(
        ConversationId conversationId, MessageAuthorKind authorKind, int sequence, CancellationToken cancellationToken)
    {
        Calls.Add((conversationId, authorKind, sequence));
        return Task.CompletedTask;
    }
}
