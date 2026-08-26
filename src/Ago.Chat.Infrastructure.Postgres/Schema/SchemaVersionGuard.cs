using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-08`: <b>this is how ordering is enforced</b>, and it is the item's most consequential choice, so
/// it is argued here and recorded in `adr/0056` rather than left implied by YAML.
///
/// <para>`adr/0056`'s open question listed two candidates - an init container on each host, or a step
/// in the deploy script - and preferred the init container because it survives a cluster rebuilt
/// without the script (`15-02`'s restore drill). This is a third form, and it dominates both: <b>the
/// host refuses to start</b>. It needs nothing from Kustomize, which has no "this Job before those
/// Deployments" primitive; nothing from the deploy script, which is not in the loop when somebody runs
/// `kubectl apply -k` by hand; and nothing from the manifest at all, which means it also protects the
/// docker-compose loop and a bare `dotnet run` - neither of which any init container could reach.
/// Kubernetes then supplies the ordering for free: the Job applies, the hosts refuse until it has, and
/// pods restart until they stop refusing.</para>
///
/// <para><b>And it answers the sub-question the ADR called the more interesting half</b> - "it
/// requires a host to be able to *state* the version it expects, and where that number comes from is
/// not obvious." The answer is that no host states anything. Each one already carries the migrations
/// its own build was compiled against (<see cref="SchemaVersionCheck"/>'s remarks), so "the version I
/// expect" is derived from the binary and cannot drift from it. A number written in a manifest could
/// disagree with the code; this cannot.</para>
///
/// <para><b>Refusing means exiting, not failing readiness.</b> Both stop traffic reaching the pod, and
/// an unready pod is the gentler of the two. It was rejected anyway: an unready pod with no logs of
/// its own is the same *shape* of failure as the incident - something is quietly not working while
/// every signal says the deploy proceeded - whereas a container that exits with this exception's
/// message in its logs is unmissable in `kubectl get pods` and names its own cause. `CrashLoopBackOff`
/// is a worse-looking state, and looking worse is the feature.</para>
/// </summary>
public static class SchemaVersionGuard
{
    /// <summary>
    /// Polls until the schema is current, or throws <see cref="SchemaOutOfDateException"/> once
    /// <see cref="SchemaGuardOptions.WaitTimeout"/> has elapsed.
    ///
    /// <para>Takes <paramref name="inspect"/> as a delegate rather than a
    /// <see cref="SchemaVersionCheck"/>, for one reason that is worth the small indirection: it makes
    /// the wait-then-refuse behaviour testable without a database at all. The interesting cases -
    /// pending on the first look and current on the third, still pending when the clock runs out - are
    /// about this loop, not about Postgres, and driving them through a real migration would be slower
    /// and would prove less.</para>
    /// </summary>
    public static async Task<SchemaStatus> EnsureCurrentAsync(
        Func<CancellationToken, Task<SchemaStatus>> inspect,
        SchemaGuardOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var status = await inspect(cancellationToken);
        var waited = false;

        while (!status.IsCurrent && started.Elapsed < options.WaitTimeout)
        {
            if (!waited)
            {
                waited = true;
                logger.LogWarning(
                    "Schema is behind this build: {PendingCount} migration(s) pending ({Pending}). "
                    + "Waiting up to {WaitTimeout}s for Ago.Chat.Migrator to apply them.",
                    status.Pending.Count, string.Join(", ", status.Pending), options.WaitTimeout.TotalSeconds);
            }

            await Task.Delay(options.PollInterval, cancellationToken);
            status = await inspect(cancellationToken);
        }

        if (!status.IsCurrent)
        {
            throw new SchemaOutOfDateException(status, started.Elapsed);
        }

        if (waited)
        {
            logger.LogInformation(
                "Schema reached the expected version after {Elapsed}s.", started.Elapsed.TotalSeconds);
        }

        // Logged every time, including the ordinary case. `8-08`'s Scope: a migration that runs
        // silently is the same operational problem as one that does not run - and the same is true of
        // the check. A log line naming the migration this pod was built against is what makes a
        // half-finished deploy readable from one pod's logs.
        logger.LogInformation(
            "Schema is current: built against {Expected}, {AppliedCount} migration(s) applied.",
            status.ExpectedLatest ?? "(none)", status.Applied.Count);

        if (status.AheadOfThisBuild.Count > 0)
        {
            // Not an error - see SchemaStatus.AheadOfThisBuild for why a rolled-back pod meeting a
            // newer schema is the expand/contract window working as designed, not a fault.
            logger.LogInformation(
                "The database is ahead of this build by {Count} migration(s) ({Ahead}). This is expected "
                + "during a rollback; expand/contract means the columns this build reads still exist.",
                status.AheadOfThisBuild.Count, string.Join(", ", status.AheadOfThisBuild));
        }

        return status;
    }
}
