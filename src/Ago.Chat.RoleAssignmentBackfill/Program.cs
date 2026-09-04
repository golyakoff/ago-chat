using Ago.Chat.RoleAssignmentBackfill;

// `22-16`: republish `RoleAssignmentsChanged` for every tenant whose operators existed before `22-05`
// shipped, and whose current permissions therefore never reached `role_assignment_projections` on the
// calendar side - none of the three real publishers (site registration, invite redemption, operator
// removal) can ever fire retroactively (docs/backlog/22-16). Same shape adr/0056 established for
// Ago.Chat.Migrator: no generic host, no DI container, no configuration binding - this process opens a
// connection, does the one thing it exists to do, says what it did, and exits.
//
// Idempotent and re-runnable by construction (RoleAssignmentProjectionBackfill's own remarks): running
// this twice restages the identical current fact for every candidate the second time, which a
// full-snapshot event and a full-replace consumer both already treat as a no-op.

var connectionString = Environment.GetEnvironmentVariable("AGO_CHAT_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync(
        "Set AGO_CHAT_CONNECTION_STRING - e.g. the docker-compose Postgres from local-dev.md.");
    return BackfillRunner.Failure;
}

using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

return await BackfillRunner.RunAsync(connectionString, Console.Out, lifetime.Token);
