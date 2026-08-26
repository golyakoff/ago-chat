using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>Why <see cref="DatabaseAvailabilityWait.UntilReadyAsync"/> stopped waiting.</summary>
public enum DatabaseAvailability
{
    /// <summary>Postgres authenticated the connection and answered a query. The only outcome that lets
    /// a migration start.</summary>
    Available,

    /// <summary>Every probe failed with something that could plausibly resolve on its own, and the
    /// budget ran out. An infrastructure problem: Postgres never arrived.</summary>
    GaveUpWaiting,

    /// <summary>A probe failed with something no amount of waiting fixes - a wrong password, a
    /// database that does not exist, a missing grant. Reported immediately, without waiting.</summary>
    WillNotResolveByWaiting,
}

/// <summary>The outcome plus the failure that produced it, so the caller can quote the provider's own
/// error rather than a summary of it.</summary>
public sealed record DatabaseAvailabilityResult(
    DatabaseAvailability Outcome, TimeSpan Elapsed, int Attempts, Exception? LastFailure);

/// <summary>
/// `8-10`: <b>the migrator waits for its database instead of losing a race to it.</b>
///
/// <para>On 2026-08-26 a deploy rolled a dozen workloads at once and the migrator Job started while
/// Postgres was still restarting. It exited non-zero with <c>Connection refused</c>, the Job's
/// <c>backoffLimit: 0</c> left it <c>Failed</c>, and the three hosts then correctly refused to start
/// against a schema older than their own build. Nothing misbehaved - but the safe outcome required a
/// person to notice and re-apply the Job, and `8-08` exists because a step requiring a person is a
/// step that gets skipped.</para>
///
/// <para><b>The distinction this type is built around.</b> `adr/0056` is right that a <em>migration</em>
/// failure must not be retried, and raising <c>backoffLimit</c> would have retried a genuinely broken
/// migration too. This was not a migration failure: the migration never started. So the fix is not to
/// retry the failure, it is to stop "the database is not there yet" from being a failure at all - and
/// the wait therefore wraps <b>only the connectivity probe</b>. By the time
/// <see cref="SchemaMigrationApplier"/> is constructed this class has already returned, so no retry
/// and no wait can reach a migration. That is the property most easily lost by wrapping a wait one
/// level too wide, and it is asserted by <c>SchemaMigratorTests</c> rather than left to this
/// paragraph.</para>
///
/// <para><b>Why in-process rather than an init container.</b> The same reasoning
/// <see cref="SchemaVersionGuard"/> records, and it transfers rather than being assumed to: an init
/// container is the conventional Kubernetes answer and cannot reach the docker-compose loop or a bare
/// <c>dotnet run</c>, which is where a developer meets this failure first. It would also have to
/// re-implement the classification below in shell - and a <c>pg_isready</c> loop cannot tell a wrong
/// password from a slow start, which is precisely the mistake this type exists to avoid.</para>
/// </summary>
public static class DatabaseAvailabilityWait
{
    /// <summary>
    /// SQLSTATEs meaning <em>Postgres is up enough to answer, and the answer is "not yet"</em>.
    ///
    /// <para>An allow-list, and that is the load-bearing decision here rather than the contents.
    /// The failure mode of wrongly failing on a transient condition is a loud, accurate error naming
    /// the provider's own message; the failure mode of wrongly waiting on a permanent one is a
    /// wrong password reported ninety seconds later as a timeout - a <em>worse</em> message than the
    /// one `8-10` set out to fix. Unknown means fail, always.</para>
    /// </summary>
    private static readonly HashSet<string> WaitableSqlStates = new(StringComparer.Ordinal)
    {
        // 57P03 cannot_connect_now - "the database system is starting up", "...is shutting down",
        // "...is in recovery mode". The open question `8-10` filed: a Postgres mid-restart can accept
        // a TCP connection and still refuse to serve. This is that answer, and it resolves by itself.
        "57P03",

        // 57P01 admin_shutdown, 57P02 crash_shutdown - the server terminated the connection because it
        // is going down or because another backend crashed. Both are a restart in progress, which is
        // the exact shape of the incident.
        "57P01",
        "57P02",

        // 53300 too_many_connections. Included after some hesitation: it is not "the database is not
        // there yet", it is "the database is there and busy". It is on the list anyway because its
        // only resolution is time - a deploy in which a dozen pods reconnect at once transiently
        // exhausts the slots - and because failing here would make the migrator the first casualty of
        // a thundering herd it did not cause. A permanently undersized max_connections still fails,
        // just ninety seconds later, with "too many connections" quoted verbatim.
        "53300",
    };

    /// <summary>
    /// Socket-level failures meaning the network path to Postgres is not established yet.
    ///
    /// <para><c>ConnectionRefused</c> is the one the incident produced - the Service exists, the pod
    /// behind it is restarting. The rest are the same condition seen from a different layer: DNS that
    /// has not caught up on a namespace applied from scratch (`15-02`'s restore drill), a SYN dropped
    /// while a node's iptables rules are in flux, a handshake cut short by a backend exiting.</para>
    ///
    /// <para><c>HostNotFound</c> is the debatable member, because a typo'd hostname produces it
    /// forever. It is included because a from-scratch <c>kubectl apply -k</c> creates the Service and
    /// this Job in the same breath and CoreDNS caches the negative answer, and because being wrong
    /// about it costs a bounded delay followed by a message that quotes <c>could not resolve host</c>
    /// verbatim - a slow correct diagnosis, not a misleading one.</para>
    /// </summary>
    private static readonly HashSet<SocketError> WaitableSocketErrors =
    [
        SocketError.ConnectionRefused,
        SocketError.ConnectionReset,
        SocketError.HostNotFound,
        SocketError.TryAgain,
        SocketError.HostUnreachable,
        SocketError.NetworkUnreachable,
        SocketError.NetworkDown,
        SocketError.TimedOut,
    ];

    /// <summary>
    /// Whether a failed connection attempt is a state to wait through.
    ///
    /// <para><b>What is deliberately absent</b>, because absence is the whole design: <c>28P01</c>
    /// (invalid password), <c>28000</c> (no <c>pg_hba.conf</c> entry, or a rejected authorisation),
    /// <c>3D000</c> (the database does not exist - creating it is not this deployable's job and it will
    /// not appear on its own), <c>42501</c> (insufficient privilege), <c>08P01</c> (protocol violation
    /// - whatever answered is not Postgres), and a malformed connection string. Each of those is
    /// permanent, each is a configuration mistake, and each would be turned into an unexplained
    /// ninety-second timeout by a more generous rule.</para>
    ///
    /// <para><b>It walks the inner-exception chain</b> rather than matching only the outermost type,
    /// because Npgsql wraps: <c>NpgsqlException("Failed to connect to host:5432")</c> carries the
    /// <see cref="SocketException"/> that actually says why, and
    /// <c>NpgsqlException("Exception while reading from stream")</c> carries the
    /// <see cref="EndOfStreamException"/>. The wrapper says nothing; the leaf says everything. The walk
    /// stops at the first link that is a verdict - a <see cref="PostgresException"/> is classified by
    /// its SQLSTATE and by nothing deeper, and a <see cref="SocketException"/> not on the list is a
    /// definite no rather than a reason to keep unwrapping.</para>
    /// </summary>
    public static bool IsWorthWaitingFor(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                // The server answered, and this is its own verdict. Classified by SQLSTATE only:
                // there is nothing deeper worth consulting, and unwrapping past it could turn a
                // 28P01 into a wait.
                case PostgresException postgres:
                    return WaitableSqlStates.Contains(postgres.SqlState);

                case SocketException socket:
                    return WaitableSocketErrors.Contains(socket.SocketErrorCode);

                // Npgsql's own connect timeout. The server never answered inside the connection
                // string's Timeout, which during a restart is indistinguishable from a dropped SYN.
                case TimeoutException:
                    return true;

                // "Exception while reading from stream" -> "Attempted to read past the end of the
                // stream": the peer closed the connection part-way through the startup handshake,
                // before saying anything a SQLSTATE could describe.
                //
                // MEASURED, not anticipated. `8-10` set out expecting `Connection refused` to be the
                // only shape of "not yet"; the first real run against a container that had just been
                // started produced this instead, because Docker's port proxy binds the published port
                // the moment the container is created and accepts connections that the Postgres behind
                // it is not yet listening for. Kubernetes has the same shape wherever something
                // terminates a connection ahead of the backend. It is the same event as
                // `ConnectionReset` above, seen as a clean EOF rather than an RST.
                //
                // The cost of being wrong about it: a connection string pointed at a port that is not
                // Postgres at all would wait the full budget and then report `EndOfStreamException`
                // against a named host and port - slow, but not misleading.
                case EndOfStreamException:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Opens a connection and runs <c>SELECT 1</c>.
    ///
    /// <para><b>Not <c>pg_isready</c>, and not a bare TCP connect.</b> A TCP connect proves a socket is
    /// open, which a Postgres still in recovery also offers; this probe authenticates, selects the
    /// database and executes, so "available" means the same thing here that it means to the migration
    /// that follows. That is also what makes a wrong password surface as <c>28P01</c> on the first
    /// probe instead of as a timeout ninety seconds later.</para>
    ///
    /// <para><c>Pooling=false</c>, because this connection is opened once, is deliberately not shared
    /// with the <c>DbContext</c> that follows, and a pool retained after a probe against a server that
    /// was mid-restart is a pool of connections to a server that no longer exists.</para>
    /// </summary>
    public static async Task ProbeAsync(string connectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    /// <summary>
    /// Probes until Postgres answers, the budget runs out, or a probe fails with something waiting
    /// cannot fix.
    ///
    /// <para>Takes <paramref name="probe"/> as a delegate for the same reason
    /// <see cref="SchemaVersionGuard.EnsureCurrentAsync"/> takes <c>inspect</c> as one: the interesting
    /// states - refused twice then accepted, refused until the clock runs out, rejected outright on the
    /// first attempt - are properties of this loop, and a real Postgres cannot be made to enter them on
    /// demand. The classification is tested separately against real
    /// <see cref="PostgresException"/> and <see cref="SocketException"/> values, and the two together
    /// cover what one slow integration test would have covered worse.</para>
    ///
    /// <para><b>The elapsed budget can overshoot by one probe.</b> The deadline is checked between
    /// attempts, not enforced on an attempt already in flight, so a probe that blocks for the
    /// connection string's own <c>Timeout</c> can carry the total past
    /// <see cref="DatabaseAvailabilityOptions.WaitTimeout"/> by that much. Cancelling a probe midway
    /// would trade a bounded overshoot for an ambiguity - a cancelled attempt has no classification at
    /// all - and the reported <see cref="DatabaseAvailabilityResult.Elapsed"/> is the real figure
    /// rather than the budget, so nothing is hidden by it.</para>
    /// </summary>
    public static async Task<DatabaseAvailabilityResult> UntilReadyAsync(
        Func<CancellationToken, Task> probe,
        DatabaseAvailabilityOptions options,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var attempts = 0;
        var announced = false;

        while (true)
        {
            attempts++;
            try
            {
                await probe(cancellationToken);
                if (announced)
                {
                    await output.WriteLineAsync(
                        $"Postgres accepted a connection after {started.Elapsed.TotalSeconds:F1}s "
                        + $"({attempts} attempt(s)). Proceeding.");
                }

                return new DatabaseAvailabilityResult(
                    DatabaseAvailability.Available, started.Elapsed, attempts, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!IsWorthWaitingFor(ex))
                {
                    return new DatabaseAvailabilityResult(
                        DatabaseAvailability.WillNotResolveByWaiting, started.Elapsed, attempts, ex);
                }

                if (started.Elapsed >= options.WaitTimeout)
                {
                    return new DatabaseAvailabilityResult(
                        DatabaseAvailability.GaveUpWaiting, started.Elapsed, attempts, ex);
                }

                if (!announced)
                {
                    announced = true;
                    // Once, on entering the wait - the same shape SchemaVersionGuard's warning takes.
                    // A line per attempt would be forty-five lines of noise in `kubectl logs` for a
                    // condition whose only interesting facts are that it started and how it ended.
                    await output.WriteLineAsync(
                        $"Postgres is not accepting connections yet ({Describe(ex)}). Waiting up to "
                        + $"{options.WaitTimeout.TotalSeconds:F0}s before giving up. No migration has "
                        + "been attempted.");
                }

                await Task.Delay(options.PollInterval, cancellationToken);
            }
        }
    }

    /// <summary>The provider's own message, one level of inner exception deep - the operator reading
    /// <c>kubectl logs</c> on a failed Job needs <c>Connection refused</c>, not a summary of it.</summary>
    public static string Describe(Exception exception)
    {
        var sqlState = exception is PostgresException postgres ? $" ({postgres.SqlState})" : string.Empty;
        var described = $"{exception.GetType().Name}{sqlState}: {exception.Message}";
        return exception.InnerException is { } inner
            ? $"{described} -> {inner.GetType().Name}: {inner.Message}"
            : described;
    }

    /// <summary>
    /// <c>host:port/database</c> from a connection string, for the give-up message. Host, port and
    /// database only: this string carries a password, everything in these repositories is public, and
    /// a diagnostic that prints a credential is a worse bug than the one it was diagnosing.
    /// </summary>
    public static string DescribeTarget(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"{builder.Host ?? "(no host)"}:{builder.Port}/{builder.Database ?? "(no database)"}";
        }
        catch (ArgumentException)
        {
            return "(unparseable connection string)";
        }
    }
}
