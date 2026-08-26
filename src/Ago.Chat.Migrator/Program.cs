using Ago.Chat.Infrastructure.Postgres.Schema;
using Ago.Chat.Migrator;

// `8-08`/`adr/0056`: the deployable that applies migrations, and the only thing that does.
//
// Argument parsing and a connection string; everything else is MigratorRunner, which a test drives
// against a real Postgres. No generic host, no DI container, no configuration binding: this process
// opens a connection, applies what is pending, says what it did, and exits.

// Same variable every other host reads (ChatModule), so the Kubernetes Job and the compose loop can
// hand it the identical value they already hand the Api. `17-03` is where this becomes *two*
// credentials - a DDL role here and a DML role there - and this item deliberately does not half-do
// that split (adr/0056's Consequences).
var connectionString = Environment.GetEnvironmentVariable("AGO_CHAT_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync(
        "Set AGO_CHAT_CONNECTION_STRING - e.g. the docker-compose Postgres from local-dev.md.");
    return MigratorRunner.Failure;
}

// --verify is the read-only mode. There is deliberately no --down and no --target: EF generates
// Down() methods and this project has never executed one, so offering a rollback flag would be
// offering a path nobody has tested, which is worse than offering none because it would be believed.
// A migration that turns out to be wrong is `15-02`'s restore (adr/0056).
var mode = args.Contains("--verify", StringComparer.Ordinal) ? MigratorMode.Verify : MigratorMode.Apply;

var unknown = args.Where(a => a is not "--verify").ToList();
if (unknown.Count > 0)
{
    await Console.Error.WriteLineAsync(
        $"Unknown argument(s): {string.Join(", ", unknown)}. Usage: Ago.Chat.Migrator [--verify]");
    return MigratorRunner.Failure;
}

// `8-10`: the one *optional* variable. adr/0056 records "reads exactly one environment variable" as a
// property worth keeping, and this does not spend it: unset means the chosen 90s default, so the set
// of variables the migrator *requires* is still exactly one. An unparseable value is refused rather
// than silently defaulted - a manifest typo that quietly restored the default would be the same class
// of drift `8-08` exists to prevent.
if (!DatabaseAvailabilityOptions.TryReadFromEnvironment(
        Environment.GetEnvironmentVariable, out var waitOptions, out var waitError))
{
    await Console.Error.WriteLineAsync(waitError);
    return MigratorRunner.Failure;
}

// Ctrl-C / SIGTERM cancels the wait for a connection, not a migration already in flight - Postgres
// runs the DDL transactionally, so an interrupted apply rolls its current migration back and the
// history table stays truthful about what completed.
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

return await MigratorRunner.RunAsync(connectionString, mode, Console.Out, lifetime.Token, waitOptions);
