using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-01`'s direct proof of `concurrency.md`'s "Operator assignment" claim: the atomic
/// `UPDATE ... WHERE active_chats &lt; capacity` never lets `active_chats` exceed `capacity`, even
/// under real concurrent load - many real connections from the pool racing the same row, not
/// sequential awaits pretending to be concurrent (`4-01`'s own Done when says exactly this).
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorCapacityStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryClaimAsync_UnderConcurrentLoad_NeverExceedsCapacity_AndClaimsExactlyCapacityMany()
    {
        const int capacity = 5;
        const int attempts = 20;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);

        var exportedMetrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(ChatMetrics.MeterName)
            .AddInMemoryExporter(exportedMetrics)
            .Build();

        // A fresh AgoChatDbContext per attempt, not one store shared across all of them - DbContext
        // is not thread-safe, and a Scoped-per-unit-of-work DbContext is exactly how this port is
        // actually used in production (4-02's per-conversation transaction), so the test should not
        // paper over that with one shared instance.
        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(async _ =>
            {
                await using var db = fixture.CreateDbContext();
                return await new OperatorCapacityStore(db).TryClaimAsync(operatorId, CancellationToken.None);
            }));

        Assert.Equal(capacity, results.Count(claimed => claimed));
        Assert.Equal(attempts - capacity, results.Count(claimed => !claimed));
        Assert.Equal(capacity, await ReadActiveChatsAsync(operatorId));

        // `7-02`'s Done-when: the same real concurrent-load run this test already proves capacity
        // correctness under also proves the attempts-vs-conflicts counters move with it - real
        // contention, not a hand-fed value.
        meterProvider.ForceFlush();
        var attemptsMetric = exportedMetrics.Single(m => m.Name == ChatMetrics.AssignmentCapacityClaimAttemptsInstrumentName);
        Assert.Equal(capacity, SumByOutcome(attemptsMetric, "claimed"));
        Assert.Equal(attempts - capacity, SumByOutcome(attemptsMetric, "conflict"));

        var conflictsMetric = exportedMetrics.Single(m => m.Name == ChatMetrics.AssignmentCapacityClaimConflictsInstrumentName);
        long conflictsTotal = 0;
        foreach (ref readonly var point in conflictsMetric.GetMetricPoints())
        {
            conflictsTotal += point.GetSumLong();
        }

        Assert.Equal(attempts - capacity, conflictsTotal);
    }

    private static long SumByOutcome(Metric metric, string outcome)
    {
        long total = 0;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            foreach (var tag in point.Tags)
            {
                if (tag.Key == "outcome" && (string?)tag.Value == outcome)
                {
                    total += point.GetSumLong();
                }
            }
        }

        return total;
    }

    [Fact]
    public async Task ReleaseAsync_DecrementsActiveChats_AndNeverGoesBelowZero()
    {
        const int capacity = 2;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await new OperatorCapacityStore(db).TryClaimAsync(operatorId, CancellationToken.None));
        }

        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorCapacityStore(db).ReleaseAsync(operatorId, CancellationToken.None);
        }
        Assert.Equal(0, await ReadActiveChatsAsync(operatorId));

        // A duplicate/racing release must not push the count negative.
        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorCapacityStore(db).ReleaseAsync(operatorId, CancellationToken.None);
        }
        Assert.Equal(0, await ReadActiveChatsAsync(operatorId));
    }

    /// <summary>
    /// `6-10`: a real `40P01`, arranged rather than waited for, and the one shape of it that is fully
    /// deterministic - <c>ReleaseAsync</c> called inside a caller-owned transaction
    /// (<c>OperatorConversationReleaser</c>'s shape, `4-04`). Two transactions take the same two
    /// <c>operators</c> rows in opposite order, which is exactly what the assignment engine's batches
    /// do to each other; the victim is pinned by giving the releasing session a 10 ms
    /// <c>deadlock_timeout</c> and the other one 30 s, so the process that runs the deadlock check
    /// first - and therefore aborts - is always the release.
    ///
    /// <para>Two things are asserted, and the second is the one that matters to an operator: the
    /// caller gets the port's own <see cref="OperatorCapacityContentionException"/> rather than a raw
    /// <c>PostgresException</c>, and <c>Attempts</c> is <c>1</c> - no retry was attempted, because
    /// there is none to attempt. The deadlock aborted the caller's whole transaction; re-issuing the
    /// statement on it could only produce `25P02 in_failed_sql_transaction`. Re-running the sweep is
    /// the consumer's redelivery's job. The close path, which owns no transaction, is the one that
    /// retries - proven under real contention in <c>CloseConversationCapacityConcurrencyTests</c>.</para>
    /// </summary>
    [Fact]
    public async Task ReleaseAsync_WhenADeadlockAbortsACallerOwnedTransaction_SurfacesTheContentionType_NeverANpgsqlError()
    {
        var (_, first) = await SeedOperatorAsync(capacity: 5);
        var (_, second) = await SeedOperatorAsync(capacity: 5);

        // Both operators must actually hold a slot, or `ReleaseAsync`'s own `AND active_chats > 0`
        // floor makes its `UPDATE` match no row on the visible snapshot - and a row an `UPDATE` never
        // intends to touch is a row it never waits for, so there would be nothing to deadlock over.
        await using (var seeding = fixture.CreateDbContext())
        {
            Assert.True(await new OperatorCapacityStore(seeding).TryClaimAsync(first, CancellationToken.None));
            Assert.True(await new OperatorCapacityStore(seeding).TryClaimAsync(second, CancellationToken.None));
        }

        // The other side of the cycle: one transaction holding `first`, about to want `second`.
        await using var other = await fixture.DataSource.OpenConnectionAsync();
        var otherTransaction = await other.BeginTransactionAsync();
        await ExecuteAsync(other, otherTransaction, "SET LOCAL deadlock_timeout = '30s'");
        await ExecuteAsync(other, otherTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{first.Value}'");

        // The releasing side: its own transaction, holding `second`, about to want `first`.
        await using var releasing = await fixture.DataSource.OpenConnectionAsync();
        var releasingTransaction = await releasing.BeginTransactionAsync();
        await ExecuteAsync(releasing, releasingTransaction, "SET LOCAL deadlock_timeout = '10ms'");
        await ExecuteAsync(releasing, releasingTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{second.Value}'");

        var otherBlocks = ExecuteAsync(other, otherTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{second.Value}'");
        await WaitUntilWaitingAsync(other.ProcessID);

        await using var db = new AgoChatDbContext(
            new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(releasing).Options);
        await db.Database.UseTransactionAsync(releasingTransaction);

        var exception = await Assert.ThrowsAsync<OperatorCapacityContentionException>(
            () => new OperatorCapacityStore(db).ReleaseAsync(first, CancellationToken.None));

        Assert.Equal(first, exception.OperatorId);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, Assert.IsType<PostgresException>(exception.InnerException).SqlState);

        await releasingTransaction.RollbackAsync();
        await otherBlocks;
        await otherTransaction.RollbackAsync();
    }

    /// <summary>
    /// `18-02`: the identical arranged deadlock as
    /// <see cref="ReleaseAsync_WhenADeadlockAbortsACallerOwnedTransaction_SurfacesTheContentionType_NeverANpgsqlError"/>
    /// above, with <see cref="OperatorCapacityStore.TryClaimAsync"/> pinned as the victim instead of
    /// <see cref="OperatorCapacityStore.ReleaseAsync"/> - proving the translation this item added to
    /// <c>TryClaimAsync</c> (previously a bare no-op-on-deadlock statement that only ever ran inside a
    /// claimer's own batch transaction, where the batch's own generic catch swallowed whatever
    /// exception type reached it) now behaves exactly like <c>ReleaseAsync</c>'s existing
    /// caller-owned-transaction branch: a real `40P01`, not a raw <c>PostgresException</c>, and
    /// <c>Attempts == 1</c> because a deadlock inside a caller-owned transaction leaves nothing to
    /// retry in place. <c>TransferConversationHandler</c> is the first caller for which this
    /// distinction is actually observable, since it is the first that must not leak Npgsql to
    /// <c>Ago.Chat.Application</c> (CLAUDE.md rule 2) rather than merely logging whatever came up a
    /// level.
    /// </summary>
    [Fact]
    public async Task TryClaimAsync_WhenADeadlockAbortsACallerOwnedTransaction_SurfacesTheContentionType_NeverANpgsqlError()
    {
        var (_, first) = await SeedOperatorAsync(capacity: 5);
        var (_, second) = await SeedOperatorAsync(capacity: 5);

        // The other side of the cycle: one transaction holding `first`, about to want `second`.
        await using var other = await fixture.DataSource.OpenConnectionAsync();
        var otherTransaction = await other.BeginTransactionAsync();
        await ExecuteAsync(other, otherTransaction, "SET LOCAL deadlock_timeout = '30s'");
        await ExecuteAsync(other, otherTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{first.Value}'");

        // The claiming side: its own transaction, holding `second`, about to want `first` -
        // TryClaimAsync's own UPDATE matches the row (fresh, capacity 5, active_chats 0), so it
        // genuinely waits on it rather than finding nothing to lock.
        await using var claiming = await fixture.DataSource.OpenConnectionAsync();
        var claimingTransaction = await claiming.BeginTransactionAsync();
        await ExecuteAsync(claiming, claimingTransaction, "SET LOCAL deadlock_timeout = '10ms'");
        await ExecuteAsync(claiming, claimingTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{second.Value}'");

        var otherBlocks = ExecuteAsync(other, otherTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{second.Value}'");
        await WaitUntilWaitingAsync(other.ProcessID);

        await using var db = new AgoChatDbContext(
            new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(claiming).Options);
        await db.Database.UseTransactionAsync(claimingTransaction);

        var exception = await Assert.ThrowsAsync<OperatorCapacityContentionException>(
            () => new OperatorCapacityStore(db).TryClaimAsync(first, CancellationToken.None));

        Assert.Equal(first, exception.OperatorId);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, Assert.IsType<PostgresException>(exception.InnerException).SqlState);

        await claimingTransaction.RollbackAsync();
        await otherBlocks;
        await otherTransaction.RollbackAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Polls <c>pg_stat_activity</c> instead of sleeping a guessed interval: the cycle only
    /// closes once the other side is genuinely parked on a lock, and a fixed delay would either be
    /// slower than it needs to be or occasionally too short.</summary>
    private async Task WaitUntilWaitingAsync(int processId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await using var command = new NpgsqlCommand(
                "SELECT wait_event_type = 'Lock' FROM pg_stat_activity WHERE pid = @pid", connection);
            command.Parameters.AddWithValue("pid", processId);
            if (await command.ExecuteScalarAsync() is true)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Process {processId} never parked on a lock.");
    }

    [Fact]
    public async Task TryClaimAsync_WhenAlreadyAtCapacity_ReturnsFalse_AndLeavesActiveChatsUnchanged()
    {
        const int capacity = 1;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await new OperatorCapacityStore(db).TryClaimAsync(operatorId, CancellationToken.None));
        }

        bool secondClaim;
        await using (var db = fixture.CreateDbContext())
        {
            secondClaim = await new OperatorCapacityStore(db).TryClaimAsync(operatorId, CancellationToken.None);
        }

        Assert.False(secondClaim);
        Assert.Equal(1, await ReadActiveChatsAsync(operatorId));
    }

    /// <summary>
    /// `23-04`'s own Done-when, at the store level directly rather than through
    /// `AssignConversationHandler`: "a take when `active_chats &gt;= capacity` succeeds and
    /// `active_chats` ends one higher" - the invariant the previous design forbade for
    /// <see cref="OperatorCapacityStore.TryClaimAsync"/>, and the entire reason
    /// <see cref="OperatorCapacityStore.ClaimAsync"/> exists as a second, compare-free method rather
    /// than a parameter on the first. Deliberately does not touch `conversation_assignments` at all -
    /// this is the one piece of `23-04`'s own new behaviour a real Postgres can still prove today,
    /// independent of the still-open `ck_conversation_assignments_source` migration gap
    /// `AssignConversationConcurrencyTests`/`data-model.md` document.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_WhenAlreadyAtCapacity_StillIncrements_PastCapacity()
    {
        const int capacity = 1;
        var (siteId, operatorId) = await SeedOperatorAsync(capacity);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await new OperatorCapacityStore(db).TryClaimAsync(operatorId, CancellationToken.None));
        }

        Assert.Equal(capacity, await ReadActiveChatsAsync(operatorId));

        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorCapacityStore(db).ClaimAsync(operatorId, CancellationToken.None);
        }

        // Past capacity, deliberately - `decisions.md` §2: "a manual claim increments active_chats and
        // does not check it."
        Assert.Equal(capacity + 1, await ReadActiveChatsAsync(operatorId));
    }

    /// <summary>An operator nobody has claimed anything for yet - `ClaimAsync`'s own unconditional
    /// `UPDATE` still has to be the very first increment cleanly, not only the "on top of an existing
    /// claim" case above.</summary>
    [Fact]
    public async Task ClaimAsync_OnAFreshOperator_IncrementsFromZero()
    {
        var (_, operatorId) = await SeedOperatorAsync(capacity: 5);

        await using (var db = fixture.CreateDbContext())
        {
            await new OperatorCapacityStore(db).ClaimAsync(operatorId, CancellationToken.None);
        }

        Assert.Equal(1, await ReadActiveChatsAsync(operatorId));
    }

    /// <summary>
    /// `23-04`: the identical arranged deadlock as
    /// <see cref="TryClaimAsync_WhenADeadlockAbortsACallerOwnedTransaction_SurfacesTheContentionType_NeverANpgsqlError"/>
    /// above, with <see cref="OperatorCapacityStore.ClaimAsync"/> pinned as the victim - proving the
    /// port's translation (a real <see cref="OperatorCapacityContentionException"/>, never a raw
    /// <c>PostgresException"</c>) applies to the compare-free write exactly as it does to
    /// <see cref="OperatorCapacityStore.TryClaimAsync"/>, and that <c>Attempts == 1</c> here too: a
    /// deadlock inside a caller-owned transaction (which is the only shape `AssignConversationHandler`
    /// ever calls this through) leaves nothing on this connection to retry in place.
    /// </summary>
    [Fact]
    public async Task ClaimAsync_WhenADeadlockAbortsACallerOwnedTransaction_SurfacesTheContentionType_NeverANpgsqlError()
    {
        var (_, first) = await SeedOperatorAsync(capacity: 5);
        var (_, second) = await SeedOperatorAsync(capacity: 5);

        await using var other = await fixture.DataSource.OpenConnectionAsync();
        var otherTransaction = await other.BeginTransactionAsync();
        await ExecuteAsync(other, otherTransaction, "SET LOCAL deadlock_timeout = '30s'");
        await ExecuteAsync(other, otherTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{first.Value}'");

        await using var claiming = await fixture.DataSource.OpenConnectionAsync();
        var claimingTransaction = await claiming.BeginTransactionAsync();
        await ExecuteAsync(claiming, claimingTransaction, "SET LOCAL deadlock_timeout = '10ms'");
        await ExecuteAsync(claiming, claimingTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{second.Value}'");

        var otherBlocks = ExecuteAsync(other, otherTransaction, $"UPDATE operators SET active_chats = active_chats WHERE id = '{second.Value}'");
        await WaitUntilWaitingAsync(other.ProcessID);

        await using var db = new AgoChatDbContext(
            new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(claiming).Options);
        await db.Database.UseTransactionAsync(claimingTransaction);

        var exception = await Assert.ThrowsAsync<OperatorCapacityContentionException>(
            () => new OperatorCapacityStore(db).ClaimAsync(first, CancellationToken.None));

        Assert.Equal(first, exception.OperatorId);
        Assert.Equal(1, exception.Attempts);
        Assert.Equal(PostgresErrorCodes.DeadlockDetected, Assert.IsType<PostgresException>(exception.InnerException).SqlState);

        await claimingTransaction.RollbackAsync();
        await otherBlocks;
        await otherTransaction.RollbackAsync();
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedOperatorAsync(int capacity)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));
        await db.SaveChangesAsync();

        return (siteId, operatorId);
    }

    private async Task<int> ReadActiveChatsAsync(OperatorId operatorId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
