using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>`24-12`'s own Done-when: the access record's own stated retention, enforced by something
/// that runs - real Postgres, the same shape <see cref="WebhookDeliveryPruneJobTests"/> already
/// establishes for its own sibling table.</summary>
[Collection(PostgresCollection.Name)]
public sealed class AccessRecordPruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);

    [Fact]
    public async Task PruneAsync_RemovesAnAccessRecord_OlderThanTheRetentionWindow()
    {
        var id = await SeedRecordAsync(occurredAt: Now - RetentionWindow - TimeSpan.FromDays(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        Assert.False(await RecordExistsAsync(id));
    }

    [Fact]
    public async Task PruneAsync_LeavesAnAccessRecord_YoungerThanTheRetentionWindowAlone()
    {
        var id = await SeedRecordAsync(occurredAt: Now - TimeSpan.FromDays(1));

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);
            Assert.True(await RecordExistsAsync(id));
        }
        finally
        {
            await DeleteRecordAsync(id);
        }
    }

    private AccessRecordPruneJob CreateJob() =>
        new(fixture.DataSource, new FixedClock(Now),
            Options.Create(new AccessRecordPruneJobOptions { RetentionWindow = RetentionWindow }),
            NullLogger<AccessRecordPruneJob>.Instance);

    private async Task<Guid> SeedRecordAsync(DateTimeOffset occurredAt)
    {
        var id = Guid.NewGuid();
        var repository = new AccessRecordRepository(fixture.DataSource);
        await repository.RecordAsync(
            new AccessRecordToWrite(
                id, occurredAt, AccessRecordKind.CrossConversationHistoryRead, new SiteId(Guid.NewGuid()),
                AccessRecordActorKind.Operator, Guid.NewGuid().ToString(), AccessRecordResourceKind.Conversation,
                Guid.NewGuid()),
            CancellationToken.None);
        return id;
    }

    private async Task<bool> RecordExistsAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "select exists(select 1 from access_records where id = @id)", new { id });
    }

    private async Task DeleteRecordAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("delete from access_records where id = @id", new { id });
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
