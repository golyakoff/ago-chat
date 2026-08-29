using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records what was applied, without any of the real applier's transaction/outbox
/// behaviour - that guarantee is proven against real Postgres in Ago.Chat.Integration.Tests
/// (testing.md: never mock the database for a guarantee the schema itself provides).</summary>
public sealed class FakeSeatChangeApplier : ISeatChangeApplier
{
    private readonly List<SeatChangeApplyRequest> _applied = [];

    public IReadOnlyList<SeatChangeApplyRequest> Applied => _applied;

    public Task ApplyImmediateIncreaseAsync(SeatChangeApplyRequest request, CancellationToken cancellationToken)
    {
        _applied.Add(request);
        return Task.CompletedTask;
    }
}
