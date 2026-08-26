namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-08`: what the database says about itself, compared against the migrations the *calling
/// assembly was compiled with*. That comparison is the whole mechanism, and it is why nothing in this
/// system has to state a schema version number anywhere (`adr/0056`'s first open question).
///
/// <para><see cref="Pending"/> is the answer to "am I behind": migrations this build knows about that
/// the database has not applied. <see cref="Applied"/> is what <c>__EFMigrationsHistory</c> holds.
/// The two lists together also make "the database is <em>ahead</em> of me" visible - see
/// <see cref="AheadOfThisBuild"/>, which is deliberately not treated as an error.</para>
/// </summary>
/// <param name="Applied">Every migration id in <c>__EFMigrationsHistory</c>, oldest first.</param>
/// <param name="Pending">Migrations compiled into this build that the database has not applied,
/// oldest first. Empty means the schema is at least as new as this build expects.</param>
/// <param name="Known">Every migration id compiled into this build, oldest first.</param>
public sealed record SchemaStatus(
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Known)
{
    /// <summary>The schema is at or beyond what this build needs. Zero exit code, and the only state
    /// in which a serving host is allowed to start.</summary>
    public bool IsCurrent => Pending.Count == 0;

    /// <summary>
    /// Migrations the database has applied that this build has never heard of.
    ///
    /// <para><b>Reported, never fatal</b> - `adr/0056`'s third open question. A pod rolled back to an
    /// older image against a newer schema is exactly the expand/contract window the ADR's Consequences
    /// section adopts on purpose: the old code selects columns that still exist, because an expand
    /// migration only added. Refusing to start here would make rollback impossible, which is the one
    /// recovery path this project actually has (`15-02`). It is surfaced in the log because "this pod
    /// is older than the schema" is worth knowing when reading one, not because anything acts on
    /// it.</para>
    /// </summary>
    public IReadOnlyList<string> AheadOfThisBuild =>
        [.. Applied.Where(id => !Known.Contains(id))];

    /// <summary>The newest migration this build carries, or <see langword="null"/> for a build with
    /// none. This is "the version I expect", derived rather than configured.</summary>
    public string? ExpectedLatest => Known.Count == 0 ? null : Known[^1];
}
