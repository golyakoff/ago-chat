using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>`23-11`'s own Scope: "a reveal record... its own retention" - enforced by something that
/// runs, real Postgres, the same shape <see cref="AccessRecordPruneJobTests"/> already establishes for
/// its own sibling table.</summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactRevealPruneJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);

    [Fact]
    public async Task PruneAsync_RemovesAContactReveal_OlderThanTheRetentionWindow()
    {
        var id = await SeedRevealAsync(occurredAt: Now - RetentionWindow - TimeSpan.FromDays(1));

        await CreateJob().PruneAsync(CancellationToken.None);

        Assert.False(await RevealExistsAsync(id));
    }

    [Fact]
    public async Task PruneAsync_LeavesAContactReveal_YoungerThanTheRetentionWindowAlone()
    {
        var id = await SeedRevealAsync(occurredAt: Now - TimeSpan.FromDays(1));

        try
        {
            await CreateJob().PruneAsync(CancellationToken.None);
            Assert.True(await RevealExistsAsync(id));
        }
        finally
        {
            await DeleteRevealAsync(id);
        }
    }

    private ContactRevealPruneJob CreateJob() =>
        new(fixture.DataSource, new FixedClock(Now),
            Options.Create(new ContactRevealPruneJobOptions { RetentionWindow = RetentionWindow }),
            NullLogger<ContactRevealPruneJob>.Instance);

    private async Task<Guid> SeedRevealAsync(DateTimeOffset occurredAt)
    {
        var id = Guid.NewGuid();
        var repository = new ContactRevealRepository(fixture.DataSource);
        await repository.RecordAsync(
            new ContactRevealToWrite(
                id, occurredAt, new SiteId(Guid.NewGuid()), Guid.NewGuid(), Guid.NewGuid(), new OperatorId(Guid.NewGuid()),
                "ConsoleContactPanel"),
            CancellationToken.None);
        return id;
    }

    private async Task<bool> RevealExistsAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "select exists(select 1 from contact_reveals where id = @id)", new { id });
    }

    private async Task DeleteRevealAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("delete from contact_reveals where id = @id", new { id });
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
