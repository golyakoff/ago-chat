using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `23-76`: an in-memory <see cref="ISiteAttachmentStorageBudget"/> mirroring
/// <see cref="FakeConversationAttachmentBudget"/>'s own faithful compare-and-set exactly, keyed by
/// <see cref="SiteId"/> instead of <see cref="ConversationId"/> - see that fake's own remarks for why
/// a handler unit test can prove the decision (was the right amount reserved for the right tenant,
/// refused with the right remaining figure) while real atomicity under concurrent load stays a claim
/// about Postgres, proven in <c>Ago.Chat.Integration.Tests.SiteAttachmentStorageBudgetStoreTests</c>.
/// </summary>
public sealed class FakeSiteAttachmentStorageBudget : ISiteAttachmentStorageBudget
{
    private readonly Dictionary<SiteId, long> _reserved = [];

    public List<(SiteId SiteId, long Bytes, long BudgetBytes)> ReserveCalls { get; } = [];

    public List<(SiteId SiteId, long Bytes)> ReleaseCalls { get; } = [];

    public void SeedReserved(SiteId siteId, long bytes) => _reserved[siteId] = bytes;

    public Task<AttachmentBudgetResult> TryReserveAsync(
        SiteId siteId, long bytes, long budgetBytes, CancellationToken cancellationToken)
    {
        ReserveCalls.Add((siteId, bytes, budgetBytes));

        var current = _reserved.GetValueOrDefault(siteId);
        if (current + bytes > budgetBytes)
        {
            return Task.FromResult(new AttachmentBudgetResult(false, Math.Max(budgetBytes - current, 0)));
        }

        _reserved[siteId] = current + bytes;
        return Task.FromResult(new AttachmentBudgetResult(true, budgetBytes - (current + bytes)));
    }

    public Task ReleaseAsync(SiteId siteId, long bytes, CancellationToken cancellationToken)
    {
        ReleaseCalls.Add((siteId, bytes));
        _reserved[siteId] = Math.Max(_reserved.GetValueOrDefault(siteId) - bytes, 0);
        return Task.CompletedTask;
    }
}
