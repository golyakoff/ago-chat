using Ago.Chat.Infrastructure.Postgres.Backfill;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.RoleAssignmentBackfill;

/// <summary>
/// `22-16`: this program's whole behaviour, separated from <c>Program.cs</c> the same way `8-08`'s
/// <c>MigratorRunner</c> is - argument-free here (there are no modes to choose, unlike the migrator's
/// <c>--verify</c>), but split out regardless so a future test can drive it against a real Postgres
/// without going through <c>Environment.GetEnvironmentVariable</c>. No DI container: one
/// <see cref="AgoChatDbContext"/>, one <see cref="RoleAssignmentProjectionBackfill"/>, exit code as the
/// contract.
/// </summary>
public static class BackfillRunner
{
    public const int Success = 0;
    public const int Failure = 1;

    public static async Task<int> RunAsync(string connectionString, TextWriter output, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new AgoChatDbContext(options);

        var backfill = new RoleAssignmentProjectionBackfill(db, new UuidV7Generator(), new RealClock());

        try
        {
            var outcome = await backfill.RunAsync(cancellationToken);

            await output.WriteLineAsync(
                $"{outcome.CandidatesConsidered} candidate operator(s) considered (currently active, "
                + "external identity linked).");
            await output.WriteLineAsync(
                $"{outcome.Published.Count} RoleAssignmentsChanged event(s) staged to the outbox.");

            if (outcome.SkippedDueToRace > 0)
            {
                await output.WriteLineAsync(
                    $"{outcome.SkippedDueToRace} candidate(s) were removed concurrently while this ran and "
                    + "were correctly left unpublished - the removal's own real-path event is the truthful "
                    + "fact for them now (RoleAssignmentProjectionBackfill's own remarks on ordering).");
            }

            // No external subject id, no site permission detail - `CLAUDE.md`'s "everything is
            // public" extends to what a `kubectl logs` capture of this Job could end up showing, and a
            // count is everything a human running this needs to see per site.
            var bySite = outcome.Published
                .GroupBy(a => a.SiteId.Value)
                .OrderBy(g => g.Key);
            foreach (var site in bySite)
            {
                await output.WriteLineAsync($"  site {site.Key}: {site.Count()} operator(s) republished");
            }

            await output.WriteLineAsync(
                "Nothing here talks to the broker - Ago.Chat.Worker's own OutboxDispatcher publishes these "
                + "on its next poll, exactly like every other publisher of this event.");

            return Success;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await output.WriteLineAsync($"BACKFILL FAILED: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                await output.WriteLineAsync($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return Failure;
        }
    }

    /// <summary>The one place `date-and-time.md`'s <see cref="IClock"/> port is implemented as
    /// <see cref="DateTimeOffset.UtcNow"/> in this project - `Ago.Platform.Hosting.SystemClock` would
    /// do the identical thing, but pulling in that project only for this one line would be exactly the
    /// dependency creep this project's csproj comment argues against; a private one-liner costs
    /// nothing and keeps the reference list this project's own architecture test asserts unchanged.
    /// </summary>
    private sealed class RealClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
