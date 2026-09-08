using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeRoleChangeRecordRepository : IRoleChangeRecordRepository
{
    public List<RoleChangeRecordToWrite> Recorded { get; } = [];

    public Task RecordAsync(RoleChangeRecordToWrite record, CancellationToken cancellationToken)
    {
        Recorded.Add(record);
        return Task.CompletedTask;
    }
}
