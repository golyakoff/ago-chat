using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records what was written, without <see cref="SiteSuspensionRecordRepository"/>'s own
/// ambient-transaction join - that guarantee is proven against real Postgres in
/// <c>Ago.Chat.Integration.Tests</c>, the same "never mock the database for a guarantee the schema
/// itself provides" precedent <see cref="FakeOutboxWriter"/>'s own remarks state.</summary>
public sealed class FakeSiteSuspensionRecordRepository : ISiteSuspensionRecordRepository
{
    private readonly List<SiteSuspensionRecordToWrite> _recorded = [];

    public IReadOnlyList<SiteSuspensionRecordToWrite> Recorded => _recorded;

    public Task RecordAsync(SiteSuspensionRecordToWrite record, CancellationToken cancellationToken)
    {
        _recorded.Add(record);
        return Task.CompletedTask;
    }
}
