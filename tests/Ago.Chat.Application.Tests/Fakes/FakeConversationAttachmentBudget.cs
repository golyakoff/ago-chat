using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `23-75`: an in-memory <see cref="IConversationAttachmentBudget"/> that mirrors the real
/// compare-and-set faithfully (reserve only if the running total would stay at or under the budget,
/// never below zero on release) - real atomicity under concurrent load is a claim about Postgres,
/// proven where it can actually be proven
/// (<c>Ago.Chat.Integration.Tests.ConversationAttachmentBudgetStoreTests</c>,
/// <c>AttachmentConversationBudgetFlowTests</c>). What a handler unit test can prove is the decision:
/// whether <c>CreateAttachmentHandler</c> reserved the right amount for the right conversation,
/// and refused with the right remaining-budget figure when it should have.
/// </summary>
public sealed class FakeConversationAttachmentBudget : IConversationAttachmentBudget
{
    private readonly Dictionary<ConversationId, long> _reserved = [];

    public List<(ConversationId ConversationId, long Bytes, long BudgetBytes)> ReserveCalls { get; } = [];

    public List<(ConversationId ConversationId, long Bytes)> ReleaseCalls { get; } = [];

    /// <summary>Seeds a conversation as already holding this many reserved bytes, so a test can put a
    /// handler call right up against the ceiling without needing ten real prior calls to get there.
    /// </summary>
    public void SeedReserved(ConversationId conversationId, long bytes) => _reserved[conversationId] = bytes;

    public Task<AttachmentBudgetResult> TryReserveAsync(
        ConversationId conversationId, long bytes, long budgetBytes, CancellationToken cancellationToken)
    {
        ReserveCalls.Add((conversationId, bytes, budgetBytes));

        var current = _reserved.GetValueOrDefault(conversationId);
        if (current + bytes > budgetBytes)
        {
            return Task.FromResult(new AttachmentBudgetResult(false, Math.Max(budgetBytes - current, 0)));
        }

        _reserved[conversationId] = current + bytes;
        return Task.FromResult(new AttachmentBudgetResult(true, budgetBytes - (current + bytes)));
    }

    public Task ReleaseAsync(ConversationId conversationId, long bytes, CancellationToken cancellationToken)
    {
        ReleaseCalls.Add((conversationId, bytes));
        _reserved[conversationId] = Math.Max(_reserved.GetValueOrDefault(conversationId) - bytes, 0);
        return Task.CompletedTask;
    }
}
