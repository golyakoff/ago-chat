namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-08`: thrown by <see cref="SchemaVersionGuard"/> when a host's own build carries migrations the
/// database has not applied. It is thrown *before* the host starts listening, so the process exits
/// non-zero and never serves a request against a schema it does not match.
///
/// <para>The message names the pending migrations rather than saying "schema out of date", because
/// the incident this exists to prevent (2026-08-25) was hard to diagnose exactly to the degree that
/// nothing named the gap: the symptom was "some queries fail", three layers away from the
/// cause.</para>
/// </summary>
public sealed class SchemaOutOfDateException(SchemaStatus status, TimeSpan waited)
    : Exception(BuildMessage(status, waited))
{
    public SchemaStatus Status { get; } = status;

    private static string BuildMessage(SchemaStatus status, TimeSpan waited) =>
        $"This host was built against migration '{status.ExpectedLatest}', and the database has not applied "
        + $"{status.Pending.Count} of the migrations it needs after waiting {waited.TotalSeconds:0.#}s: "
        + $"{string.Join(", ", status.Pending)}. "
        + "Run Ago.Chat.Migrator against this database before starting this host - it is the only thing "
        + "that applies migrations (adr/0056). Refusing to start: serving traffic against an older schema "
        + "returns 200s for pages whose queries fail, which is the failure this check exists to prevent.";
}
