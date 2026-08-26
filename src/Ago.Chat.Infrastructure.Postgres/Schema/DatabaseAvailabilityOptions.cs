namespace Ago.Chat.Infrastructure.Postgres.Schema;

/// <summary>
/// `8-10`: how long <see cref="DatabaseAvailabilityWait"/> treats "Postgres is not accepting
/// connections" as a state to wait through rather than a reason to fail.
///
/// <para>Deliberately <b>not</b> an <c>IOptions</c>-bound class like <see cref="SchemaGuardOptions"/>.
/// `adr/0056` records as a feature that <c>Ago.Chat.Migrator</c> reads exactly one required
/// environment variable and builds no configuration pipeline at all; adding one here to carry two
/// timespans would spend that property to buy nothing. <c>Program.cs</c> reads one optional variable
/// and constructs this directly.</para>
/// </summary>
public sealed class DatabaseAvailabilityOptions
{
    /// <summary>
    /// The environment variable <c>Ago.Chat.Migrator</c> reads to override
    /// <see cref="WaitTimeout"/>. Optional: unset means <see cref="DefaultWaitTimeout"/>, so the
    /// migrator still has exactly one variable it *requires*.
    /// </summary>
    public const string WaitTimeoutVariable = "AGO_CHAT_DB_WAIT_TIMEOUT";

    /// <summary>
    /// <b>Ninety seconds, and here is where the number comes from.</b>
    ///
    /// <para><b>The floor is measured, on a container.</b> `8-10` timed
    /// <c>postgres:17-alpine</c> on this project's development machine, warm image, three runs each,
    /// with this class's own two-second <see cref="PollInterval"/> - so each figure is an upper bound
    /// on the true readiness moment, which lies within one poll below it:</para>
    ///
    /// <list type="bullet">
    /// <item><c>docker run</c> on an empty data directory, through <c>initdb</c>: <b>4.2-4.3s</b> to an
    /// authenticated <c>SELECT 1</c>.</item>
    /// <item><c>SIGKILL</c> then <c>docker start</c> on a database holding a 300k-row table, so WAL
    /// crash recovery had real work: <b>4.3s</b>. Every attempt before the last was refused with
    /// <c>57P03 the database system is starting up</c>.</item>
    /// </list>
    ///
    /// <para><b>What the budget is actually sized for is not measured, and is stated as such</b>
    /// (CLAUDE.md rule 7). The case that produced `8-10` was a Postgres <em>pod</em> restarting during a
    /// twelve-workload rollout, which adds pod scheduling, a volume re-attach and a node under load
    /// from eleven other rollouts - none of it measured here, and the one component that was measured
    /// (crash recovery) scales with write volume rather than being a constant. Ninety seconds is
    /// roughly twenty times the measured container figure, and it is a judgement bounded on both sides
    /// by things that are known:</para>
    ///
    /// <list type="bullet">
    /// <item>It must comfortably exceed the measured 1.6s floor and the seconds-scale pod scheduling
    /// around it, or the wait would not survive the case it was built for.</item>
    /// <item>It must stay well under <c>redeploy.sh</c>'s <c>kubectl wait --timeout=300s</c> on this
    /// Job, so that a migrator which gave up reports <em>its own</em> reason before the deploy script
    /// reports the less informative "the Job did not complete".</item>
    /// </list>
    ///
    /// <para>It is deliberately longer than <see cref="SchemaGuardOptions.WaitTimeout"/>'s 60s, and
    /// the two are not coupled: a host that gives up first restarts and tries again, which is the
    /// behaviour `8-08` designed. Nothing breaks if this outlives that; what would break is a migrator
    /// that gave up before Postgres had finished starting, which is the whole failure being fixed.</para>
    /// </summary>
    public static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(90);

    /// <summary>How long to keep re-probing before giving up. Exceeding it is a real failure that
    /// stops the deploy - the point is to tell <em>not yet</em> from <em>not going to</em>, not to
    /// wait forever.</summary>
    public TimeSpan WaitTimeout { get; init; } = DefaultWaitTimeout;

    /// <summary>
    /// How long to pause between probes. Two seconds for the same reason
    /// <see cref="SchemaGuardOptions.PollInterval"/> is: a refused connection fails in milliseconds,
    /// so without a pause this would be a spin loop against a socket, and with a much longer one the
    /// migrator would idle for seconds after Postgres was already up.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads <see cref="WaitTimeoutVariable"/>, falling back to <see cref="DefaultWaitTimeout"/> when
    /// it is absent, and refusing an unparseable value rather than silently defaulting - a typo in a
    /// manifest that quietly restored the default would be exactly the kind of drift `8-08` exists to
    /// prevent.
    /// </summary>
    public static bool TryReadFromEnvironment(
        Func<string, string?> readVariable, out DatabaseAvailabilityOptions options, out string? error)
    {
        var raw = readVariable(WaitTimeoutVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            options = new DatabaseAvailabilityOptions();
            error = null;
            return true;
        }

        if (!TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < TimeSpan.Zero)
        {
            options = new DatabaseAvailabilityOptions();
            error = $"{WaitTimeoutVariable} is not a valid non-negative timespan (got '{raw}'). "
                + "Use hh:mm:ss - e.g. 00:01:30.";
            return false;
        }

        options = new DatabaseAvailabilityOptions { WaitTimeout = parsed };
        error = null;
        return true;
    }
}
