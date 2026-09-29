using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`26-278`: the identical "records what was applied, no transaction/outbox behaviour" shape
/// <see cref="FakeAdministratorSlotChangeApplier"/> already establishes - that guarantee (a real,
/// Succeeded option row plus a real grant, one transaction) is proven against real Postgres in
/// <c>Ago.Chat.Integration.Tests</c> instead (testing.md: never mock the database for a guarantee the
/// schema itself provides).</summary>
public sealed class FakeChannelAddOnPurchaseApplier : IChannelAddOnPurchaseApplier
{
    private readonly List<ChannelAddOnPurchaseApplyRequest> _applied = [];

    public IReadOnlyList<ChannelAddOnPurchaseApplyRequest> Applied => _applied;

    public Task ApplyPurchaseAsync(ChannelAddOnPurchaseApplyRequest request, CancellationToken cancellationToken)
    {
        _applied.Add(request);
        return Task.CompletedTask;
    }
}
