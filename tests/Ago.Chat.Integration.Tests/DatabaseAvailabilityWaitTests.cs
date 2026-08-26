using System.Diagnostics;
using System.Net.Sockets;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `8-10`: the wait-then-give-up loop, driven directly - the same shape, and for the same reason, as
/// <see cref="SchemaVersionGuardTests"/> does for `8-08`'s guard. The interesting states (refused twice
/// then accepted, refused until the clock runs out, rejected outright on the first attempt) are
/// properties of this loop, and a real Postgres cannot be made to enter them on demand.
///
/// <para>The end-to-end proof - a migrator process started against a port nothing is listening on,
/// which then waits and succeeds - is <c>SchemaMigratorTests</c>, against a real container. These two
/// files divide the work the way `8-08` divided its own: the loop here, the deployable there.</para>
/// </summary>
public class DatabaseAvailabilityWaitTests
{
    private static readonly DatabaseAvailabilityOptions Impatient = new()
    {
        WaitTimeout = TimeSpan.FromMilliseconds(400),
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private static Exception NotYet() =>
        new NpgsqlException("Failed to connect", new SocketException((int)SocketError.ConnectionRefused));

    private static Exception NotEver() => new PostgresException(
        "password authentication failed for user \"ago\"", "FATAL", "FATAL", "28P01");

    private static Task<DatabaseAvailabilityResult> RunAsync(
        Func<int, Task> probe, TextWriter output, DatabaseAvailabilityOptions? options = null)
    {
        var attempts = 0;
        return DatabaseAvailabilityWait.UntilReadyAsync(
            _ => probe(++attempts), options ?? Impatient, output, CancellationToken.None);
    }

    [Fact]
    public async Task WhenPostgresAnswersImmediately_ItProbesOnceAndSaysNothing()
    {
        await using var output = new StringWriter();

        var result = await RunAsync(_ => Task.CompletedTask, output);

        Assert.Equal(DatabaseAvailability.Available, result.Outcome);
        Assert.Equal(1, result.Attempts);
        // Silence on the happy path is deliberate: the migrator's log is read when something went
        // wrong, and a line saying the database was reachable is noise in front of the report that
        // matters. It is also what makes the wait free to have added.
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// <b>The behaviour the item exists for.</b> The database is not there for the first two probes and
    /// is there for the third; that is a normal state to wait through, not a failure, so the run
    /// succeeds.
    /// </summary>
    [Fact]
    public async Task WhenPostgresArrivesLate_ItWaitsAndThenSucceeds()
    {
        await using var output = new StringWriter();

        var result = await RunAsync(
            attempt => attempt < 3 ? Task.FromException(NotYet()) : Task.CompletedTask, output);

        Assert.Equal(DatabaseAvailability.Available, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.Contains("not accepting connections yet", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("accepted a connection after", output.ToString(), StringComparison.Ordinal);
        // Says so while it is happening, not only afterwards: an operator watching a Job for ninety
        // seconds needs to know it is waiting on purpose.
        Assert.Contains("No migration has been attempted", output.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <em>Not going to be</em>, as opposed to <em>not yet</em>: the budget is real, and exceeding it
    /// is a genuine failure that still stops the deploy.
    /// </summary>
    [Fact]
    public async Task WhenPostgresNeverArrives_ItGivesUpWithinTheBudget()
    {
        await using var output = new StringWriter();
        var elapsed = Stopwatch.StartNew();

        var result = await RunAsync(_ => Task.FromException(NotYet()), output);

        Assert.Equal(DatabaseAvailability.GaveUpWaiting, result.Outcome);
        Assert.True(result.Attempts > 1, $"expected repeated probes, got {result.Attempts}");
        Assert.NotNull(result.LastFailure);
        // Bounded generously rather than tightly - this asserts that the budget is honoured at all,
        // which is the property; asserting a millisecond figure would make it a timing test.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"the wait must be bounded by WaitTimeout; it took {elapsed.Elapsed}");
    }

    /// <summary>
    /// <b>The open question `8-10` filed, as a behaviour.</b> A wrong password must not be waited on:
    /// it is reported on the first attempt, so the operator reads "password authentication failed"
    /// rather than a timeout ninety seconds later.
    /// </summary>
    [Fact]
    public async Task WhenTheCredentialIsWrong_ItReportsAtOnceWithoutWaiting()
    {
        await using var output = new StringWriter();
        var patient = new DatabaseAvailabilityOptions
        {
            WaitTimeout = TimeSpan.FromMinutes(5),
            PollInterval = TimeSpan.FromSeconds(30),
        };
        var elapsed = Stopwatch.StartNew();

        var result = await RunAsync(_ => Task.FromException(NotEver()), output, patient);

        Assert.Equal(DatabaseAvailability.WillNotResolveByWaiting, result.Outcome);
        Assert.Equal(1, result.Attempts);
        // A five-minute budget was on offer and none of it was spent. That is the assertion; the
        // outcome enum alone would pass just as well against a loop that waited and then gave up.
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"a permanent failure must not consume the wait budget; it took {elapsed.Elapsed}");
        // And it never announced a wait it was not going to perform.
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// A permanent failure after some waiting is still permanent. The realistic sequence: the port is
    /// refused while the pod restarts, then Postgres answers and rejects the credential.
    /// </summary>
    [Fact]
    public async Task WhenAWaitEndsInAPermanentFailure_ItStopsThereRatherThanRunningTheBudgetOut()
    {
        await using var output = new StringWriter();

        var result = await RunAsync(
            attempt => Task.FromException(attempt < 3 ? NotYet() : NotEver()), output);

        Assert.Equal(DatabaseAvailability.WillNotResolveByWaiting, result.Outcome);
        Assert.Equal(3, result.Attempts);
        Assert.IsType<PostgresException>(result.LastFailure);
    }

    /// <summary>A zero budget must still probe once and report on what it found, rather than refusing
    /// without looking - mirrors <c>SchemaVersionGuardTests.WithAZeroWaitTimeout_ItStillInspectsOnce</c>,
    /// and is the configuration a fast-failing caller would choose.</summary>
    [Fact]
    public async Task WithAZeroBudget_ItStillProbesOnce()
    {
        await using var output = new StringWriter();
        var options = new DatabaseAvailabilityOptions
        {
            WaitTimeout = TimeSpan.Zero,
            PollInterval = TimeSpan.Zero,
        };

        var result = await RunAsync(_ => Task.FromException(NotYet()), output, options);

        Assert.Equal(DatabaseAvailability.GaveUpWaiting, result.Outcome);
        Assert.Equal(1, result.Attempts);
    }

    /// <summary>
    /// Cancellation is not a classification. Ctrl-C or a SIGTERM from Kubernetes propagates out rather
    /// than being reported as "gave up waiting" - the deployable was stopped, it did not fail, and
    /// saying otherwise would put a false infrastructure failure in the log of an ordinary shutdown.
    /// </summary>
    [Fact]
    public async Task WhenCancelled_ItPropagatesRatherThanReportingAFailure()
    {
        await using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseAvailabilityWait.UntilReadyAsync(
                _ =>
                {
                    cancellation.Cancel();
                    return Task.FromException(NotYet());
                },
                Impatient, output, cancellation.Token));
    }

    /// <summary>
    /// The environment override, including the half that matters: an unparseable value is refused, not
    /// silently replaced by the default. A manifest typo that quietly restored the default is the same
    /// class of drift `8-08` exists to prevent.
    /// </summary>
    [Fact]
    public void TheWaitTimeoutOverride_DefaultsWhenAbsentAndRefusesWhenMalformed()
    {
        Assert.True(DatabaseAvailabilityOptions.TryReadFromEnvironment(_ => null, out var absent, out _));
        Assert.Equal(DatabaseAvailabilityOptions.DefaultWaitTimeout, absent.WaitTimeout);

        Assert.True(DatabaseAvailabilityOptions.TryReadFromEnvironment(
            _ => "00:00:30", out var set, out _));
        Assert.Equal(TimeSpan.FromSeconds(30), set.WaitTimeout);

        Assert.False(DatabaseAvailabilityOptions.TryReadFromEnvironment(
            _ => "ninety seconds", out _, out var error));
        Assert.Contains(DatabaseAvailabilityOptions.WaitTimeoutVariable, error!, StringComparison.Ordinal);
    }
}
