using System.ComponentModel.DataAnnotations;

namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// Bound from <c>SchemaGuard:*</c> (naming-and-structure.md's options convention). Every value here
/// is a starting point rather than a measured number (CLAUDE.md rule 7) - what is *not* a guess is
/// the shape: a bounded wait, then a refusal.
/// </summary>
public sealed class SchemaGuardOptions
{
    public const string SectionName = "SchemaGuard";

    /// <summary>
    /// <b>Leave this on.</b> It exists for one case: a developer pointing a host at a database they
    /// are mid-migration on, where crashing is noise rather than signal. It is deliberately not
    /// wired into any manifest, so turning it off in a deployed environment takes an edit somebody
    /// has to write down - which is the point. `redeploy.md` says so too.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to keep re-checking while migrations are still pending, before refusing.
    ///
    /// <para><b>Why wait at all, rather than fail immediately.</b> The migrator Job and the host
    /// Deployments are applied together, so a host can genuinely reach its first check before the Job
    /// has finished - that is not an error, it is a race with a known winner. Failing instantly would
    /// hand that race to Kubernetes' restart backoff, which doubles to a five-minute cap: a Job taking
    /// three minutes could leave every host sitting in backoff for five more *after* the schema was
    /// ready. This wait is the in-process form of the init container `adr/0056` floated, and it costs
    /// exactly nothing on the happy path, where the first check passes.</para>
    ///
    /// <para>Sixty seconds is not measured. It is longer than any migration this project has ever run
    /// and shorter than the backoff cap it exists to avoid.</para>
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:30:00")]
    public TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often to re-inspect while waiting. Each poll is one small <c>SELECT</c> against
    /// <c>__EFMigrationsHistory</c>.</summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
}
