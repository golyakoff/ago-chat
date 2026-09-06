using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Mirrors the real <c>ConversationBlockRepository</c>'s own three-way, atomic contract: a
/// conversation that does not exist for the given site answers <see cref="ConversationBlockOutcome.NotFound"/>;
/// one that already sits in the state being asked for answers <see cref="ConversationBlockOutcome.AlreadyInState"/>
/// and changes nothing; otherwise the transition applies and a record is appended to
/// <see cref="Records"/> - the same "one row per act, never per state" shape the real table has
/// (`ConversationBlockRecordKind`'s own remarks).</summary>
public sealed class FakeConversationBlockRepository : IConversationBlockRepository
{
    private readonly Dictionary<(ConversationId, SiteId), OperatorId?> _blockedBy = [];

    public List<(Guid RecordId, ConversationId ConversationId, ConversationBlockRecordKind Kind, OperatorId ActorId, DateTimeOffset OccurredAt)> Records { get; } = [];

    public void SeedConversation(ConversationId conversationId, SiteId siteId, bool blocked = false) =>
        _blockedBy[(conversationId, siteId)] = blocked ? new OperatorId(Guid.NewGuid()) : null;

    public Task<ConversationBlockOutcome> BlockAsync(
        ConversationId conversationId, SiteId siteId, OperatorId blockedBy, Guid blockRecordId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = (conversationId, siteId);
        if (!_blockedBy.TryGetValue(key, out var current))
        {
            return Task.FromResult(ConversationBlockOutcome.NotFound);
        }

        if (current is not null)
        {
            return Task.FromResult(ConversationBlockOutcome.AlreadyInState);
        }

        _blockedBy[key] = blockedBy;
        Records.Add((blockRecordId, conversationId, ConversationBlockRecordKind.Blocked, blockedBy, now));
        return Task.FromResult(ConversationBlockOutcome.Applied);
    }

    public Task<ConversationBlockOutcome> UnblockAsync(
        ConversationId conversationId, SiteId siteId, OperatorId unblockedBy, Guid blockRecordId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var key = (conversationId, siteId);
        if (!_blockedBy.TryGetValue(key, out var current))
        {
            return Task.FromResult(ConversationBlockOutcome.NotFound);
        }

        if (current is null)
        {
            return Task.FromResult(ConversationBlockOutcome.AlreadyInState);
        }

        _blockedBy[key] = null;
        Records.Add((blockRecordId, conversationId, ConversationBlockRecordKind.Unblocked, unblockedBy, now));
        return Task.FromResult(ConversationBlockOutcome.Applied);
    }
}
