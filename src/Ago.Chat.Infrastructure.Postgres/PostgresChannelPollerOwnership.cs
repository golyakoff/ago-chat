using System.Collections.Concurrent;
using System.Data;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `adr/0089`'s adapter: a session-scoped PostgreSQL advisory lock, keyed per
/// <see cref="ChannelCredentialId"/> via <see cref="AdvisoryLockKey"/>, on one dedicated connection
/// held open for the life of this Worker process. Registered <c>Singleton</c>
/// (<c>ServiceCollectionExtensions.AddPostgresPersistence</c>) so <c>TelegramLongPollingService</c> and
/// <c>MaxLongPollingService</c> share the identical instance rather than each opening its own
/// connection - `adr/0089`'s "one connection per Worker process, not one per credential or one per
/// channel" is only true if both services resolve the same object.
///
/// <para><b>One connection per process, not per credential (`adr/0089`'s own stated trade-off).</b>
/// Many advisory locks can be held on a single session, and every acquire/verify/release here goes
/// through <see cref="_connection"/> - opened once from the process's own <see cref="NpgsqlDataSource"/>
/// pool and never returned to it (never disposed) for as long as this instance is alive. That is the
/// entire mechanism: PostgreSQL releases every advisory lock a session holds when that session ends.
/// <see cref="NpgsqlConnection"/> is not safe for concurrent use, and several poller loops (Telegram's
/// and MAX's, potentially several credentials each) touch this one connection - so every access is
/// serialised through <see cref="_gate"/> rather than each caller assuming exclusive use of it.</para>
///
/// <para><b>A lease is bound to the session that granted it, not to whichever connection
/// <see cref="_connection"/> currently points at.</b> Found by review, not by a failing test in
/// production, and reproduced deliberately before being fixed
/// (<c>SessionReplacedUnderALiveLease_MakesTheStaleLeaseDetectable</c>,
/// <c>Ago.Chat.Concurrency.Tests</c>): "does this process still own credential X's lock" is <em>not</em>
/// the same question as "is <see cref="_connection"/> currently open", because
/// <see cref="EnsureConnectionAsync"/> replaces a dead connection with a fresh one on the very next
/// <see cref="TryAcquireAsync"/> call from <em>any</em> credential's loop - including one that has
/// nothing to do with the lease being checked. A lease acquired on the old (now-replaced) session would
/// see the new connection open and wrongly report itself still held, even though its own lock died with
/// the session it was acquired on and was never re-acquired on the new one. <see cref="_generation"/>
/// closes this: incremented only when <see cref="EnsureConnectionAsync"/> actually opens a new physical
/// connection, captured by a lease at acquire time, and compared on every
/// <see cref="VerifyStillHeldAsync"/>/<see cref="ReleaseAsync"/> call - a mismatch means the session that
/// granted this lease is gone, full stop, regardless of whether some other, newer session happens to be
/// open right now.</para>
///
/// <para><b>Trap: a pooled connection silently drops the lock.</b> <c>NpgsqlDataSource.OpenConnectionAsync</c>
/// hands back a connection checked out of the pool; Npgsql only resets session state - which includes
/// releasing every advisory lock - when that connection is closed/disposed and returned to the pool.
/// Holding it open in <see cref="_connection"/> for this instance's entire lifetime, never inside a
/// <c>using</c>/<c>await using</c> block, is what keeps it out of the pool's reset path.
/// <c>PooledConnection_ReturnedToPool_ReleasesTheAdvisoryLock</c> (<c>Ago.Chat.Concurrency.Tests</c>,
/// inside <c>ChannelPollerOwnershipConcurrencyTests</c>) proves the failure mode this class exists to
/// avoid: acquiring on a connection that IS returned to the pool between operations loses the lock,
/// verified against a real container rather than trusted from this comment.</para>
///
/// <para><b>The <c>bigint</c> key, and making a collision observable (`adr/0089`'s own negative
/// consequence).</b> A 64-bit hash collision between two different credentials is negligible at this
/// system's scale but not zero, and `adr/0089` requires it be observable rather than merely improbable.
/// <see cref="_observedKeys"/> is this process's own record of which credential it last computed each
/// key for; <see cref="RecordAndCheckCollision"/> logs at <see cref="LogLevel.Critical"/>, naming both
/// colliding credential ids and the shared key, the moment this process itself computes the same key
/// for two different credentials. This does not detect every collision - a collision this one process
/// never observes both sides of stays silent to it - but it is strictly better than never checking at
/// all, and it needs no infrastructure beyond what this class already holds (no leases table, no
/// second Redis role, both of which `adr/0089` declines for other reasons already).</para>
/// </summary>
public sealed class PostgresChannelPollerOwnership(
    NpgsqlDataSource dataSource, ILogger<PostgresChannelPollerOwnership> logger)
    : IChannelPollerOwnership, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<long, ChannelCredentialId> _observedKeys = new();
    private NpgsqlConnection? _connection;

    /// <summary>Bumped only inside <see cref="EnsureConnectionAsync"/>, only when it actually opens a
    /// new physical connection - never on every call. A lease captures this at acquire time; a mismatch
    /// on a later check means the session that granted it is gone, even if <see cref="_connection"/>
    /// itself is open right now (it would be a <em>different</em>, newer session). See this class's own
    /// remarks, "a lease is bound to the session that granted it".</summary>
    private long _generation;

    private bool _disposed;

    public async Task<IChannelPollerLease?> TryAcquireAsync(ChannelCredentialId credentialId, CancellationToken cancellationToken)
    {
        var key = AdvisoryLockKey.For(credentialId);
        RecordAndCheckCollision(key, credentialId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Up to one retry, immediately, on a freshly (re-)opened connection. Found live while
            // writing ChannelPollerOwnershipConcurrencyTests, not anticipated by design: Npgsql's
            // NpgsqlConnection.State can still read Open for a connection PostgreSQL has already
            // terminated server-side (pg_terminate_backend, or an ordinary half-open TCP session) -
            // the client only discovers this once it actually tries to use the socket, which is exactly
            // what the command below does. EnsureConnectionAsync's State check therefore is not enough
            // on its own; retrying once, forcing a genuinely fresh connection, covers that window
            // without turning this into an unbounded retry loop - a second consecutive failure is a
            // real, different problem and is left to propagate rather than hidden.
            for (var attempt = 0; ; attempt++)
            {
                var connection = await EnsureConnectionAsync(cancellationToken);
                var generation = _generation; // snapshot after ensuring, under the same gate hold - see _generation's own remarks.
                try
                {
                    await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
                    command.Parameters.AddWithValue(key);
                    var acquired = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;

                    return acquired ? new PostgresChannelPollerLease(this, credentialId, key, generation) : null;
                }
                catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
                {
                    if (_connection is not null)
                    {
                        await _connection.DisposeAsync();
                        _connection = null;
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called only by <see cref="PostgresChannelPollerLease.VerifyStillHeldAsync"/> - see
    /// <see cref="IChannelPollerLease.VerifyStillHeldAsync"/>'s own remarks for the half-open-connection
    /// case this guards against.
    ///
    /// <para>Two independent checks, in order, because either alone is insufficient. First,
    /// <paramref name="generation"/> against <see cref="_generation"/>: if this process has already
    /// opened a <em>newer</em> session since this lease was granted - e.g. because a different
    /// credential's <see cref="TryAcquireAsync"/> call found the old one dead and replaced it - this
    /// lease's own lock died with the old session and was never re-acquired on the new one, and the new
    /// session being open right now proves nothing about it (this class's own remarks, "a lease is bound
    /// to the session that granted it"). Second, a round trip (<c>SELECT 1</c>) on whatever session
    /// generation currently matches, not a re-acquire: re-running <c>pg_try_advisory_lock</c> on the same
    /// session would trivially succeed again regardless of whether anything is wrong (advisory locks are
    /// re-entrant per session - the same fact that makes a naive two-instance test pass vacuously, see
    /// the concurrency tests' own negative control), so it would prove nothing on its own.</para></summary>
    internal async Task VerifyStillHeldAsync(ChannelCredentialId credentialId, long generation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (generation != _generation)
            {
                throw new ChannelPollerLeaseLostException(
                    $"The PostgreSQL session that granted credential {credentialId.Value}'s poll lease " +
                    $"has since been replaced by a newer one on this process; that lease's own lock was " +
                    $"never re-acquired on the new session and cannot be assumed still held.",
                    new InvalidOperationException("Session generation mismatch."));
            }

            if (_connection is not { State: ConnectionState.Open })
            {
                throw new ChannelPollerLeaseLostException(
                    $"The PostgreSQL session backing this process's poll-ownership connection is no " +
                    $"longer open; credential {credentialId.Value}'s lease cannot be confirmed.",
                    new InvalidOperationException("Connection not open."));
            }

            await using var command = new NpgsqlCommand("SELECT 1", _connection);
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not ChannelPollerLeaseLostException and not OperationCanceledException)
        {
            throw new ChannelPollerLeaseLostException(
                $"Lost the PostgreSQL session backing credential {credentialId.Value}'s poll-ownership lease.", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Called only by <see cref="PostgresChannelPollerLease.DisposeAsync"/>. Best-effort and
    /// silent on failure by design (`adr/0089`'s "release promptly on clean shutdown" - a failing
    /// release must never block or fault a shutdown path): if this throws, the session is already on
    /// its way out and PostgreSQL will release the lock itself when it ends, which is the same outcome
    /// a successful explicit unlock produces, just not as promptly.
    ///
    /// <para>Checks <paramref name="generation"/> first, and skips the unlock entirely on a mismatch -
    /// not merely as an optimisation, and proved rather than assumed
    /// (<c>DisposingAStaleLease_DoesNotRevokeAFreshLeaseForTheSameCredential</c>,
    /// <c>Ago.Chat.Concurrency.Tests</c>, inside <c>ChannelPollerOwnershipConcurrencyTests</c>). If a
    /// newer session has replaced the one this lease was granted on, that newer session may since have
    /// granted a fresh, legitimately-held lease for the very same credential (the credential's own loop
    /// having been reaped and retried). Issuing
    /// <c>pg_advisory_unlock</c> for this stale lease's key on the <em>new</em> session would release
    /// that fresh lease's lock instead of a no-op - a stale <see cref="IAsyncDisposable.DisposeAsync"/>
    /// revoking a currently-valid lease for the same credential. There is nothing this lease's own key
    /// ever held on the current session, so there is nothing to release.</para></summary>
    internal async Task ReleaseAsync(long key, long generation)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (generation != _generation || _connection is not { State: ConnectionState.Open })
            {
                return;
            }

            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", _connection);
            command.Parameters.AddWithValue(key);
            await command.ExecuteScalarAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not explicitly release advisory lock {Key}; the session will release it on close.", key);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller must already hold <see cref="_gate"/>.</summary>
    private async Task<NpgsqlConnection> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: ConnectionState.Open })
        {
            return _connection;
        }

        if (_connection is not null)
        {
            // A previous session died (the half-open-connection case, or an ordinary transient fault) -
            // every advisory lock it held is already gone with it. Dispose the dead handle and open a
            // fresh one; every credential this process was polling loses its lease and is picked up
            // again, by whichever process's TryAcquireAsync reaches it first, on the next refresh tick.
            // _generation's own bump below is what makes that loss detectable for every OTHER lease
            // still referencing the old session too, not only the one whose own call triggered this -
            // see this class's own remarks, "a lease is bound to the session that granted it".
            await _connection.DisposeAsync();
        }

        // Deliberately held open, never `await using` - see this class's own remarks, "trap: a pooled
        // connection silently drops the lock". This is the one connection adr/0089 accepts holding
        // outside NpgsqlDataSource's pooling for the process's entire lifetime.
        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        _generation++;
        return _connection;
    }

    internal bool RecordAndCheckCollision(long key, ChannelCredentialId credentialId)
    {
        var owner = _observedKeys.GetOrAdd(key, credentialId);
        if (owner == credentialId)
        {
            return false;
        }

        logger.LogCritical(
            "Advisory-lock key collision (adr/0089): ChannelCredentialId {FirstCredentialId} and " +
            "{SecondCredentialId} both hash to advisory-lock key {Key}. Whichever of the two this " +
            "process is not currently polling will never be polled by it while the other is held here - " +
            "and the same collision holds on every other process too, so neither bot may ever be polled " +
            "by anyone until this is resolved.",
            owner.Value, credentialId.Value, key);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_connection is not null)
            {
                // Closing releases every advisory lock this session still held (adr/0089's own release
                // mechanism) - explicit per-credential unlocks already happened in
                // PostgresChannelPollerLease as each poll loop stopped; this is the backstop for
                // whatever, if anything, did not (e.g. a process kill that skipped graceful shutdown
                // entirely, where nothing here runs at all and PostgreSQL's own session-end is what
                // actually releases everything).
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

/// <summary>One lease granted by <see cref="PostgresChannelPollerOwnership"/>. Kept in the same file:
/// it is that class's own implementation detail, never constructed anywhere else, and never referenced
/// by its concrete type outside it (callers hold <see cref="IChannelPollerLease"/>).
///
/// <para><paramref name="generation"/> is the session generation <see cref="PostgresChannelPollerOwnership"/>
/// was on at the moment this lease was granted - captured once, here, and never re-read. It is what lets
/// <see cref="VerifyStillHeldAsync"/>/<see cref="DisposeAsync"/> tell "the session that granted me" apart
/// from "whatever session happens to be open on the owner right now", which are not the same question
/// once the owner has replaced a dead connection - see <see cref="PostgresChannelPollerOwnership"/>'s own
/// remarks.</para></summary>
internal sealed class PostgresChannelPollerLease(
    PostgresChannelPollerOwnership owner, ChannelCredentialId credentialId, long key, long generation)
    : IChannelPollerLease
{
    private int _disposed;

    public ChannelCredentialId CredentialId { get; } = credentialId;

    public Task VerifyStillHeldAsync(CancellationToken cancellationToken) =>
        owner.VerifyStillHeldAsync(CredentialId, generation, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await owner.ReleaseAsync(key, generation);
    }
}
