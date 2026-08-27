using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>`15-04`: the outbox pattern's consumer-side counterpart - `inbox`, keyed
/// <c>(message_id, consumer)</c>, so its bounded-batch delete goes through <c>ctid</c> rather than a
/// single-column <c>id</c> (<see cref="InboxPruneQuery"/>'s own remarks). Real Postgres
/// (`testing.md`).</summary>
[Collection(PostgresCollection.Name)]
public sealed class InboxPruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromHours(24);

    [Fact]
    public async Task PruneAsync_RemovesARecord_OlderThanTheRetentionWindow()
    {
        var (messageId, consumer) = await SeedAsync(processedAt: Now - RetentionWindow - TimeSpan.FromMinutes(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        Assert.False(await RecordExistsAsync(messageId, consumer));
    }

    [Fact]
    public async Task PruneAsync_LeavesARecord_YoungerThanTheRetentionWindowAlone()
    {
        var (messageId, consumer) = await SeedAsync(processedAt: Now - TimeSpan.FromMinutes(1));

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);

            Assert.True(await RecordExistsAsync(messageId, consumer));
        }
        finally
        {
            await DeleteAsync(messageId, consumer);
        }
    }

    /// <summary>The same message can legitimately be recorded by more than one consumer
    /// (`InboxRecordConfiguration`'s own remarks: keyed by (message id, consumer), not message id
    /// alone) - proves pruning one consumer's record for a message never touches another consumer's
    /// record for that same message.</summary>
    [Fact]
    public async Task PruneAsync_PrunesPerConsumer_NotPerMessage()
    {
        var messageId = Guid.NewGuid();
        await SeedAsync(processedAt: Now - RetentionWindow - TimeSpan.FromMinutes(1), messageId, "consumer-old");
        await SeedAsync(processedAt: Now - TimeSpan.FromMinutes(1), messageId, "consumer-recent");

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);

            Assert.False(await RecordExistsAsync(messageId, "consumer-old"));
            Assert.True(await RecordExistsAsync(messageId, "consumer-recent"));
        }
        finally
        {
            await DeleteAsync(messageId, "consumer-recent");
        }
    }

    private InboxPruneJob CreateJob() =>
        new(fixture.DataSource, new FixedClock(Now),
            Options.Create(new InboxPruneJobOptions { RetentionWindow = RetentionWindow }),
            NullLogger<InboxPruneJob>.Instance);

    private async Task<(Guid MessageId, string Consumer)> SeedAsync(
        DateTimeOffset processedAt, Guid? messageId = null, string? consumer = null)
    {
        var id = messageId ?? Guid.NewGuid();
        var name = consumer ?? $"consumer-{Guid.NewGuid():N}";

        await using var db = fixture.CreateDbContext();
        db.Set<InboxRecord>().Add(new InboxRecord(id, name, processedAt));
        await db.SaveChangesAsync(CancellationToken.None);
        return (id, name);
    }

    private async Task<bool> RecordExistsAsync(Guid messageId, string consumer)
    {
        await using var db = fixture.CreateDbContext();
        return await db.Set<InboxRecord>().AnyAsync(
            r => r.MessageId == messageId && r.Consumer == consumer, CancellationToken.None);
    }

    private async Task DeleteAsync(Guid messageId, string consumer)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Set<InboxRecord>().FindAsync([messageId, consumer], CancellationToken.None);
        if (row is not null)
        {
            db.Remove(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
