using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Infrastructure.Postgres.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Migrator;

/// <summary>What one invocation was asked to do.</summary>
public enum MigratorMode
{
    /// <summary>Apply every pending migration. The default, and the only mode that writes.</summary>
    Apply,

    /// <summary>Report whether the schema is current and change nothing. Exists so the same image can
    /// answer "is this database ready" in a script or a smoke test without being the thing that makes
    /// it ready.</summary>
    Verify,
}

/// <summary>
/// `8-08`: the migrator's whole behaviour, separated from <c>Program.cs</c> so it can be driven from a
/// test against a real Postgres. `adr/0056`: "it opens a connection, applies what is pending, reports
/// what it did, and exits" - this is that, and <see cref="Program"/> is only argument parsing and a
/// connection string.
///
/// <para><b>Exit codes are the contract</b>, so they are named here rather than left as bare integers
/// at the call sites: <see cref="Success"/> when the schema is at the version this build expects
/// (whether or not anything was applied), <see cref="Failure"/> when it is not. `adr/0056` requires a
/// non-zero exit to stop a deploy rather than be retried into a crash loop, which is why the Kubernetes
/// Job carries <c>backoffLimit: 0</c>.</para>
///
/// <para><b>No dependency-injection container at all.</b> Two objects and a
/// <c>DbContextOptionsBuilder</c> is the whole graph, and a container would add a startup surface (and
/// a set of options to validate) to a process whose value is that it does one thing and stops. This is
/// the same construction <c>AgoChatDbContextFactory</c> already uses for design-time
/// <c>dotnet ef</c>.</para>
/// </summary>
public static class MigratorRunner
{
    public const int Success = 0;
    public const int Failure = 1;

    public static async Task<int> RunAsync(
        string connectionString,
        MigratorMode mode,
        TextWriter output,
        CancellationToken cancellationToken,
        DatabaseAvailabilityOptions? wait = null)
    {
        // `8-10`: the wait is here, in front of everything, and it is the *only* thing it wraps. The
        // migration below is reached with a connection already proven to authenticate and answer, so a
        // failure past this point is a migration failure and is reported and exited on immediately -
        // never retried, never waited on. `adr/0056`'s no-retry property survives precisely because
        // this returns before the DbContext is built.
        var availability = await DatabaseAvailabilityWait.UntilReadyAsync(
            token => DatabaseAvailabilityWait.ProbeAsync(connectionString, token),
            wait ?? new DatabaseAvailabilityOptions(),
            output,
            cancellationToken);

        if (availability.Outcome != DatabaseAvailability.Available)
        {
            await ReportUnavailableAsync(availability, connectionString, output);
            return Failure;
        }

        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AgoChatDbContext(options);

        var check = new SchemaVersionCheck(db);

        try
        {
            return mode == MigratorMode.Verify
                ? await VerifyAsync(check, output, cancellationToken)
                : await ApplyAsync(db, check, output, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Caught and reported rather than allowed to escape as an unhandled exception: the exit
            // code is the deliverable, and an unhandled exception in .NET exits with a platform-
            // dependent code that is not this contract's Failure. The message still goes out in full,
            // because the operator reading `kubectl logs` on a failed Job needs the provider's own
            // error, not a summary of it.
            await output.WriteLineAsync($"MIGRATION FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                await output.WriteLineAsync($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return Failure;
        }
    }

    /// <summary>
    /// `8-10`: the two failures that are <b>not</b> migration failures, and they are worded so that the
    /// first token of the first line tells them apart in <c>kubectl logs</c>.
    ///
    /// <para>The item's whole premise is that "gave up waiting for Postgres" and "the migration threw"
    /// need different reactions - one is an infrastructure problem, the other is a code problem - and
    /// that before this change both read as <c>MIGRATION FAILED</c>. Every line below therefore says
    /// explicitly that no migration was attempted, because the operator's first question on a
    /// <c>Failed</c> Job is whether the database was left half-changed.</para>
    /// </summary>
    private static async Task ReportUnavailableAsync(
        DatabaseAvailabilityResult availability, string connectionString, TextWriter output)
    {
        var target = DatabaseAvailabilityWait.DescribeTarget(connectionString);
        var last = availability.LastFailure is null
            ? "(no error recorded)"
            : DatabaseAvailabilityWait.Describe(availability.LastFailure);

        if (availability.Outcome == DatabaseAvailability.GaveUpWaiting)
        {
            await output.WriteLineAsync(
                $"WAITING FOR DATABASE FAILED: gave up after {availability.Elapsed.TotalSeconds:F1}s and "
                + $"{availability.Attempts} attempt(s) waiting for Postgres at {target} to accept "
                + "connections.");
            await output.WriteLineAsync($"  last attempt: {last}");
            await output.WriteLineAsync(
                "  No migration was attempted and the schema is unchanged. This is an infrastructure "
                + "problem, not a migration problem: check that Postgres is running and reachable, then "
                + "re-run this Job.");
            return;
        }

        await output.WriteLineAsync(
            $"CANNOT CONNECT TO DATABASE: Postgres at {target} rejected the connection with something "
            + "waiting will not fix.");
        await output.WriteLineAsync($"  {last}");
        await output.WriteLineAsync(
            "  No migration was attempted and the schema is unchanged. Reported immediately rather than "
            + "waited on, because a wrong credential, a missing database or a missing grant does not "
            + "become correct with time.");
    }

    private static async Task<int> ApplyAsync(
        AgoChatDbContext db, SchemaVersionCheck check, TextWriter output, CancellationToken cancellationToken)
    {
        var applier = new SchemaMigrationApplier(db, check);
        var outcome = await applier.ApplyAsync(cancellationToken);

        if (outcome.Applied.Count == 0)
        {
            // The idempotent case, and the common one - the Job runs on every deploy, not only the
            // deploys that need it, because a conditional step is a step that gets skipped.
            await output.WriteLineAsync(
                $"Schema already current at '{outcome.After.ExpectedLatest}'; {outcome.After.Applied.Count} "
                + "migration(s) applied previously, nothing to do.");
            return Success;
        }

        await output.WriteLineAsync($"Applied {outcome.Applied.Count} migration(s):");
        foreach (var migration in outcome.Applied)
        {
            await output.WriteLineAsync($"  + {migration}");
        }

        await output.WriteLineAsync($"Schema is now at '{outcome.After.ExpectedLatest}'.");
        return outcome.After.IsCurrent ? Success : Failure;
    }

    private static async Task<int> VerifyAsync(
        SchemaVersionCheck check, TextWriter output, CancellationToken cancellationToken)
    {
        var status = await check.InspectAsync(cancellationToken);
        if (status.IsCurrent)
        {
            await output.WriteLineAsync($"Schema is current at '{status.ExpectedLatest}'.");
            return Success;
        }

        await output.WriteLineAsync(
            $"Schema is behind: {status.Pending.Count} migration(s) pending against a build that expects "
            + $"'{status.ExpectedLatest}':");
        foreach (var migration in status.Pending)
        {
            await output.WriteLineAsync($"  ! {migration}");
        }

        return Failure;
    }
}
