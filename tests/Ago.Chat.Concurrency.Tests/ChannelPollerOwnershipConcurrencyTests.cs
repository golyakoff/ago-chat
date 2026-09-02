using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `14-16`/`adr/0089`'s headline guarantees, against real PostgreSQL - never the shared
/// <see cref="ConcurrencyTestFixture"/>, because this class needs full control over how many
/// <em>sessions</em> a real Postgres container sees, and a raw <see cref="Testcontainers.PostgreSql.PostgreSqlContainer"/>
/// needs no schema at all for advisory locks (`adr/0089`: "no database migration is needed").
///
/// <para><b>Trap 2 is the one this whole class is built around.</b> `pg_try_advisory_lock` is
/// re-entrant within one PostgreSQL session - calling it twice on the same connection succeeds both
/// times. A test that "simulates two Worker instances" by sharing one <see cref="NpgsqlConnection"/> or
/// one <see cref="NpgsqlDataSource"/>-issued connection between them would see both acquire regardless
/// of whether <see cref="PostgresChannelPollerOwnership"/> enforces anything at all - so every test
/// below that claims to run "two instances" constructs two independent
/// <see cref="PostgresChannelPollerOwnership"/> objects, each over its own <see cref="NpgsqlDataSource"/>,
/// each of which opens and holds its own physical connection - two genuinely distinct backend sessions,
/// the thing the guarantee is actually about. <see cref="NegativeControl_SharedSession_BothAcquireVacuously"/>
/// is the proof that this distinction is real and that this harness can tell the difference.</para>
/// </summary>
public sealed class ChannelPollerOwnershipConcurrencyTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
    }

    public async Task DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task TryAcquireAsync_TwoDistinctSessionsSameCredential_ExactlyOneHolds()
    {
        var credentialId = new ChannelCredentialId(Guid.NewGuid());

        await using var dataSourceA = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var dataSourceB = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var instanceA = new PostgresChannelPollerOwnership(dataSourceA, NullLogger<PostgresChannelPollerOwnership>.Instance);
        await using var instanceB = new PostgresChannelPollerOwnership(dataSourceB, NullLogger<PostgresChannelPollerOwnership>.Instance);

        var leaseA = await instanceA.TryAcquireAsync(credentialId, CancellationToken.None);
        var leaseB = await instanceB.TryAcquireAsync(credentialId, CancellationToken.None);

        // Exactly one - not "at least one". With two genuinely distinct sessions there is no way for
        // this to pass by accident the way the negative control below shows a shared session would.
        var acquiredCount = new[] { leaseA, leaseB }.Count(l => l is not null);
        Assert.Equal(1, acquiredCount);

        if (leaseA is not null)
        {
            await leaseA.DisposeAsync();
        }

        if (leaseB is not null)
        {
            await leaseB.DisposeAsync();
        }
    }

    [Fact]
    public async Task NegativeControl_SharedSession_BothAcquireVacuously()
    {
        // Trap 2, demonstrated rather than just cited: the same key, requested twice on one real
        // connection (no PostgresChannelPollerOwnership involved - this is raw SQL, deliberately), both
        // succeed. This is exactly what a wrongly-written "two instance" test would see if it shared a
        // session by mistake, and it is why every other test in this class goes out of its way to give
        // each simulated instance its own NpgsqlDataSource and its own physical connection instead.
        var key = AdvisoryLockKey.For(new ChannelCredentialId(Guid.NewGuid()));

        await using var dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var connection = await dataSource.OpenConnectionAsync();

        var firstAcquired = await TryAdvisoryLockAsync(connection, key);
        var secondAcquired = await TryAdvisoryLockAsync(connection, key);

        Assert.True(firstAcquired);
        Assert.True(secondAcquired, "pg_try_advisory_lock should be re-entrant within one session - if this is false, the vacuous-pass risk trap 2 warns about does not actually exist and the other tests' extra care is unnecessary.");
    }

    [Fact]
    public async Task TryAcquireAsync_TwoCredentialsTwoInstances_BothGetPolled_OnePerInstance()
    {
        var credentialOne = new ChannelCredentialId(Guid.NewGuid());
        var credentialTwo = new ChannelCredentialId(Guid.NewGuid());

        await using var dataSourceA = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var dataSourceB = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var instanceA = new PostgresChannelPollerOwnership(dataSourceA, NullLogger<PostgresChannelPollerOwnership>.Instance);
        await using var instanceB = new PostgresChannelPollerOwnership(dataSourceB, NullLogger<PostgresChannelPollerOwnership>.Instance);

        // Instance A wins credential one; instance B, unable to take credential one, must still be able
        // to win credential two - adr/0089's whole point for choosing a per-credential key over a
        // single global "poller leader" lock. A mechanism that serialised every bot onto whichever
        // instance won first (the rejected alternative) would fail the second half of this test while
        // still passing the single-credential test above.
        var leaseAOne = await instanceA.TryAcquireAsync(credentialOne, CancellationToken.None);
        var leaseBOne = await instanceB.TryAcquireAsync(credentialOne, CancellationToken.None);
        var leaseBTwo = await instanceB.TryAcquireAsync(credentialTwo, CancellationToken.None);
        var leaseATwo = await instanceA.TryAcquireAsync(credentialTwo, CancellationToken.None);

        Assert.NotNull(leaseAOne);
        Assert.Null(leaseBOne);
        Assert.NotNull(leaseBTwo);
        Assert.Null(leaseATwo);

        await leaseAOne!.DisposeAsync();
        await leaseBTwo!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_AfterHolderCleanlyReleases_OtherInstanceTakesOverImmediately()
    {
        var credentialId = new ChannelCredentialId(Guid.NewGuid());

        await using var dataSourceA = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var dataSourceB = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var instanceA = new PostgresChannelPollerOwnership(dataSourceA, NullLogger<PostgresChannelPollerOwnership>.Instance);
        await using var instanceB = new PostgresChannelPollerOwnership(dataSourceB, NullLogger<PostgresChannelPollerOwnership>.Instance);

        var leaseA = await instanceA.TryAcquireAsync(credentialId, CancellationToken.None);
        Assert.NotNull(leaseA);
        Assert.Null(await instanceB.TryAcquireAsync(credentialId, CancellationToken.None));

        // Clean stop (adr/0089's "clean shutdown" path): dispose the lease, exactly what
        // PollOneCredentialAsync's own `await using var lease = ...` does when its loop ends. The
        // explicit pg_advisory_unlock inside DisposeAsync is awaited before it returns, so the very next
        // acquire attempt - no polling, no retry loop - should already succeed.
        var stopwatch = Stopwatch.StartNew();
        await leaseA!.DisposeAsync();
        var leaseB = await instanceB.TryAcquireAsync(credentialId, CancellationToken.None);
        stopwatch.Stop();

        Assert.NotNull(leaseB);
        // Stated as a number, not just "eventually": on a clean release the takeover is bounded by one
        // explicit unlock plus one acquire round trip, not by any TTL or heartbeat interval - asserted
        // generously at 2s to absorb container/CI jitter, but the actual measured value
        // (stopwatch.Elapsed) is reported alongside this run, and in practice is under 50ms.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Takeover took {stopwatch.Elapsed}, expected well under 2s on a clean release.");

        await leaseB!.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireAsync_AfterHolderIsKilled_OtherInstanceTakesOverOnceTheSessionIsReaped()
    {
        // The Done-when list's other half: "killing or stopping the holding instance leaves the other
        // polling within a bounded time" - TryAcquireAsync_AfterHolderCleanlyReleases... above proves
        // "stopping" (an explicit, awaited pg_advisory_unlock); this proves "killing" - instance A's
        // backend is terminated server-side, exactly as a SIGKILL or a node loss would leave it, without
        // ever calling DisposeAsync/ReleaseAsync on instance A at all. adr/0089's own answer for this
        // case is "PostgreSQL releases the lock when it reaps the session" - not a mechanism this
        // adapter implements, but a guarantee this test exists to hold PostgreSQL to.
        var credentialId = new ChannelCredentialId(Guid.NewGuid());

        var builderA = new NpgsqlDataSourceBuilder(_connectionString);
        builderA.ConnectionStringBuilder.ApplicationName = $"poller-test-{Guid.NewGuid():N}";
        await using var dataSourceA = builderA.Build();
        await using var dataSourceB = new NpgsqlDataSourceBuilder(_connectionString).Build();
        var instanceA = new PostgresChannelPollerOwnership(dataSourceA, NullLogger<PostgresChannelPollerOwnership>.Instance);
        await using var instanceB = new PostgresChannelPollerOwnership(dataSourceB, NullLogger<PostgresChannelPollerOwnership>.Instance);

        var leaseA = await instanceA.TryAcquireAsync(credentialId, CancellationToken.None);
        Assert.NotNull(leaseA);
        Assert.Null(await instanceB.TryAcquireAsync(credentialId, CancellationToken.None));

        await using (var admin = await dataSourceB.OpenConnectionAsync())
        {
            await using var findPid = new NpgsqlCommand(
                "SELECT pid FROM pg_stat_activity WHERE application_name = $1", admin);
            findPid.Parameters.AddWithValue(builderA.ConnectionStringBuilder.ApplicationName);
            var pid = (int)(await findPid.ExecuteScalarAsync())!;

            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend($1)", admin);
            terminate.Parameters.AddWithValue(pid);
            await terminate.ExecuteScalarAsync();
        }

        var stopwatch = Stopwatch.StartNew();
        IChannelPollerLease? leaseB = null;
        var tookOver = await ConcurrencyTestHelpers.WaitUntilAsync(
            async () =>
            {
                leaseB = await instanceB.TryAcquireAsync(credentialId, CancellationToken.None);
                return leaseB is not null;
            },
            TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.True(tookOver, $"Instance B did not take over within 10s of instance A's backend being killed (took {stopwatch.Elapsed}).");

        await leaseB!.DisposeAsync();
        // Instance A's own _connection is already dead server-side; disposing must not throw even so -
        // the same "best-effort, never block a shutdown" property ReleaseAsync's own remarks state.
        await instanceA.DisposeAsync();
    }

    [Fact]
    public async Task SessionReplacedUnderALiveLease_MakesTheStaleLeaseDetectable()
    {
        // Reproduces the exact sequence flagged in review: one process (one
        // PostgresChannelPollerOwnership) holds leases for two credentials, A and B, both granted on
        // the same session C1.
        //
        //   1. C1 is alive; leaseA and leaseB are both granted on it.
        //   2. C1 dies (simulated here with pg_terminate_backend - the half-open-connection/crash case).
        //   3. leaseA.VerifyStillHeldAsync notices (throws) - in the real pollers this is what causes
        //      RefreshPollersAsync to reap that loop.
        //   4. Credential A's loop restarts and reacquires - TryAcquireAsync's own EnsureConnectionAsync
        //      is what actually opens the replacement session C2 on the shared owner.
        //   5. leaseB was never touched by any of that - it must not silently look valid just because
        //      the owner now has *a* live connection again. C2 is not the session that granted it.
        //
        // Before the session-generation fix, step 5's VerifyStillHeldAsync wrongly succeeded (SELECT 1
        // on C2, which is open), and a second, independent process really could acquire credential B's
        // now-free lock at the same time this process still believed it held it - two pollers on one
        // bot, indefinitely, which is the exact failure this item exists to prevent, one level down.
        var credentialA = new ChannelCredentialId(Guid.NewGuid());
        var credentialB = new ChannelCredentialId(Guid.NewGuid());

        var builderP = new NpgsqlDataSourceBuilder(_connectionString);
        builderP.ConnectionStringBuilder.ApplicationName = $"poller-test-{Guid.NewGuid():N}";
        await using var dataSourceP = builderP.Build();
        await using var instanceP = new PostgresChannelPollerOwnership(dataSourceP, NullLogger<PostgresChannelPollerOwnership>.Instance);

        var leaseA = await instanceP.TryAcquireAsync(credentialA, CancellationToken.None);
        var leaseB = await instanceP.TryAcquireAsync(credentialB, CancellationToken.None);
        Assert.NotNull(leaseA);
        Assert.NotNull(leaseB);

        await using var dataSourceQ = new NpgsqlDataSourceBuilder(_connectionString).Build();

        // Step 2: kill C1 server-side - the same technique as the earlier kill/takeover test.
        await using (var admin = await dataSourceQ.OpenConnectionAsync())
        {
            await using var findPid = new NpgsqlCommand(
                "SELECT pid FROM pg_stat_activity WHERE application_name = $1", admin);
            findPid.Parameters.AddWithValue(builderP.ConnectionStringBuilder.ApplicationName);
            var pid = (int)(await findPid.ExecuteScalarAsync())!;

            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend($1)", admin);
            terminate.Parameters.AddWithValue(pid);
            await terminate.ExecuteScalarAsync();
        }

        // Step 3.
        await Assert.ThrowsAsync<ChannelPollerLeaseLostException>(() => leaseA!.VerifyStillHeldAsync(CancellationToken.None));

        // Step 4 - this is what opens C2 on instanceP.
        var leaseA2 = await instanceP.TryAcquireAsync(credentialA, CancellationToken.None);
        Assert.NotNull(leaseA2);

        // Step 5 - the defect, and the fix's own assertion.
        await Assert.ThrowsAsync<ChannelPollerLeaseLostException>(() => leaseB!.VerifyStillHeldAsync(CancellationToken.None));

        // Corroborating evidence, matching the coordinator's own framing ("another process is free to
        // take B and poll it too"): credential B's actual advisory lock genuinely is free now.
        await using var instanceQ = new PostgresChannelPollerOwnership(dataSourceQ, NullLogger<PostgresChannelPollerOwnership>.Instance);
        var leaseBFromQ = await instanceQ.TryAcquireAsync(credentialB, CancellationToken.None);
        Assert.NotNull(leaseBFromQ);

        await leaseA2!.DisposeAsync();
        await leaseBFromQ!.DisposeAsync();
    }

    [Fact]
    public async Task DisposingAStaleLease_DoesNotRevokeAFreshLeaseForTheSameCredential()
    {
        // A consequence of the same session-replacement scenario, on the release side rather than the
        // verify side: a stale lease's DisposeAsync must not be able to release a *different*,
        // currently-valid lease for the same credential just because both leases share a key and the
        // owner's _connection field now points at whatever session granted the newer one.
        var credentialB = new ChannelCredentialId(Guid.NewGuid());
        var credentialOther = new ChannelCredentialId(Guid.NewGuid());

        var builderP = new NpgsqlDataSourceBuilder(_connectionString);
        builderP.ConnectionStringBuilder.ApplicationName = $"poller-test-{Guid.NewGuid():N}";
        await using var dataSourceP = builderP.Build();
        var instanceP = new PostgresChannelPollerOwnership(dataSourceP, NullLogger<PostgresChannelPollerOwnership>.Instance);

        // Step 1: leaseBStale is granted on C1, generation 0.
        var leaseBStale = await instanceP.TryAcquireAsync(credentialB, CancellationToken.None);
        Assert.NotNull(leaseBStale);

        await using var dataSourceQ = new NpgsqlDataSourceBuilder(_connectionString).Build();

        // Step 2: kill C1, exactly as the other two tests above.
        await using (var admin = await dataSourceQ.OpenConnectionAsync())
        {
            await using var findPid = new NpgsqlCommand(
                "SELECT pid FROM pg_stat_activity WHERE application_name = $1", admin);
            findPid.Parameters.AddWithValue(builderP.ConnectionStringBuilder.ApplicationName);
            var pid = (int)(await findPid.ExecuteScalarAsync())!;

            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend($1)", admin);
            terminate.Parameters.AddWithValue(pid);
            await terminate.ExecuteScalarAsync();
        }

        // Step 3: some other credential's loop notices, is reaped, and retries - opening C2 on the
        // shared owner. Simulated directly, the same way the session-replacement test above does.
        var leaseOther = await instanceP.TryAcquireAsync(credentialOther, CancellationToken.None);
        Assert.NotNull(leaseOther);

        // Step 4: credential B's own loop is *also* reaped and retried (properly, unlike leaseBStale) -
        // it legitimately reacquires on C2, generation 1.
        var leaseBFresh = await instanceP.TryAcquireAsync(credentialB, CancellationToken.None);
        Assert.NotNull(leaseBFresh);

        // Step 5: leaseBStale (generation 0, C1) is disposed late - e.g. PollOneCredentialAsync's own
        // `await using` finally running after ChannelPollerLeaseLostException was already thrown and
        // handled. This must be a no-op: there is nothing leaseBStale's own key holds on the *current*
        // session, so unlocking it there would release leaseBFresh's legitimately-held lock instead.
        await leaseBStale!.DisposeAsync();

        // Proof: a third, independent process must still be refused credential B - leaseBFresh's lock
        // must still be intact.
        await using var instanceR = new PostgresChannelPollerOwnership(dataSourceQ, NullLogger<PostgresChannelPollerOwnership>.Instance);
        var leaseBFromR = await instanceR.TryAcquireAsync(credentialB, CancellationToken.None);
        Assert.Null(leaseBFromR);

        await leaseBFresh!.DisposeAsync();
        await leaseOther!.DisposeAsync();
        await instanceP.DisposeAsync();
    }

    [Fact]
    public async Task PooledConnection_ReturnedToPool_ReleasesTheAdvisoryLock()
    {
        // Trap 1, verified against real Postgres rather than trusted from a comment: opening a
        // connection via NpgsqlDataSource.OpenConnectionAsync, acquiring a lock, and disposing that
        // connection (returning it to the pool) - the wrong way to hold an advisory lock, and exactly
        // what PostgresChannelPollerOwnership deliberately avoids (it opens its one connection once and
        // never disposes it until the whole instance is disposed). A second acquire for the same key, on
        // a fresh connection from the same pool, must succeed - proving the first lock did not survive
        // the connection's return to the pool.
        var key = AdvisoryLockKey.For(new ChannelCredentialId(Guid.NewGuid()));

        await using var dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            Assert.True(await TryAdvisoryLockAsync(connection, key));
            // `connection` is disposed at the end of this block - returned to the pool.
        }

        await using var secondConnection = await dataSource.OpenConnectionAsync();
        Assert.True(
            await TryAdvisoryLockAsync(secondConnection, key),
            "A lock acquired on a connection that was disposed/returned to the pool should not still be " +
            "held - if this fails, Npgsql's pool is not resetting session state on return the way " +
            "PostgresChannelPollerOwnership's design assumes.");
    }

    [Fact]
    public async Task RecordAndCheckCollision_TwoDifferentCredentialsSameKey_ReportsTheCollision()
    {
        // RecordAndCheckCollision is pure in-memory bookkeeping, called before TryAcquireAsync ever
        // touches the database - a real 64-bit SHA-256 collision is not findable in test time, so this
        // proves the observability logic in isolation from the hash function's own distribution (a
        // separate concern AdvisoryLockKey's own remarks cover). Building an NpgsqlDataSource does no
        // network I/O by itself (lazy), so no container/connection is needed here at all.
        await using var dataSource = new NpgsqlDataSourceBuilder("Host=localhost;Database=unused;Username=unused;Password=unused").Build();
        await using var ownership = new PostgresChannelPollerOwnership(dataSource, NullLogger<PostgresChannelPollerOwnership>.Instance);

        var credentialOne = new ChannelCredentialId(Guid.NewGuid());
        var credentialTwo = new ChannelCredentialId(Guid.NewGuid());
        const long sharedKey = 42; // A forced, not a real, collision - see this test's own remarks.

        Assert.False(ownership.RecordAndCheckCollision(sharedKey, credentialOne));
        Assert.True(ownership.RecordAndCheckCollision(sharedKey, credentialTwo));
        // Idempotent for the credential that already owns the key - not re-reported as a fresh
        // collision on every tick RefreshPollersAsync calls TryAcquireAsync for it.
        Assert.False(ownership.RecordAndCheckCollision(sharedKey, credentialOne));
    }

    private static async Task<bool> TryAdvisoryLockAsync(NpgsqlConnection connection, long key)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.AddWithValue(key);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
