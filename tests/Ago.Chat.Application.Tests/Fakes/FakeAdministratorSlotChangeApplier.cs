using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`25-41`: the identical "records what was applied, no transaction/outbox behaviour" shape
/// <see cref="FakeSeatChangeApplier"/> already establishes - that guarantee is proven against real
/// Postgres in <c>Ago.Chat.Integration.Tests</c> instead (testing.md: never mock the database for a
/// guarantee the schema itself provides).</summary>
public sealed class FakeAdministratorSlotChangeApplier : IAdministratorSlotChangeApplier
{
    private readonly List<AdministratorSlotChangeApplyRequest> _applied = [];

    public IReadOnlyList<AdministratorSlotChangeApplyRequest> Applied => _applied;

    public Task ApplyImmediateIncreaseAsync(AdministratorSlotChangeApplyRequest request, CancellationToken cancellationToken)
    {
        _applied.Add(request);
        return Task.CompletedTask;
    }
}
