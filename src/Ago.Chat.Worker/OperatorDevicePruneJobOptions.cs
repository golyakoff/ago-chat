namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OperatorDevicePruneJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).</summary>
public sealed class OperatorDevicePruneJobOptions
{
    public const string SectionName = "OperatorDevicePruneJob";

    /// <summary>
    /// `26-123`/`adr/0185`: the fourth revocation cause `adr/0179` §1 originally said did not exist -
    /// a row with <c>last_seen_at</c> older than this, still live, is revoked regardless of push
    /// provider. Exists because `26-83`/`26-122` found RuStore answers `200 OK` for a send to some
    /// tokens a reinstall leaves behind, so "the provider says the token is gone"
    /// (<c>push-notifications.md</c>'s own outcome table) structurally cannot reach them - this is the
    /// backstop for exactly that gap, not a general-purpose staleness policy.
    ///
    /// <para>14 days is checked against a real, verified refresh cadence, not invented (`CLAUDE.md`:
    /// "do not invent numbers"). <see cref="Domain.OperatorDevice.LastSeenAt"/>'s only writer is
    /// <c>OperatorDevice.Refresh</c>, called by <c>RegisterOperatorDeviceHandler</c> on every
    /// registration - and `ago-android`'s <c>WorkManagerDeviceRegistrationScheduler</c> calls that
    /// endpoint once every 24 hours regardless of push activity or foreground use, precisely so a
    /// rotation missed while the app was not running is still caught
    /// (`push-notifications.md`'s own "three places" the client registers from). A live device with any
    /// network therefore refreshes at least daily; this default is fourteen times that cadence - wide
    /// enough that ordinary lost connectivity (travel, a SIM swap, Doze deferring one run) is never
    /// mistaken for abandonment, narrow enough that a genuinely dead registration is still caught well
    /// inside the window `26-83` needs closed. See `adr/0185` for the full reasoning.</para>
    /// </summary>
    public TimeSpan Threshold { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How often a prune cycle runs. This table changes slowly (one row per operator device,
    /// written on registration or a push outcome) and the threshold above is itself wide, so unlike
    /// <see cref="OutboxPruneJobOptions.Interval"/>'s ten-minute cadence on a hot table, an hour is
    /// frequent enough that a newly-stale row is never left live for long relative to
    /// <see cref="Threshold"/>, and infrequent enough to add no meaningful load - the same reasoning
    /// <see cref="AccessRecordPruneJobOptions.Interval"/> already gives for its own slow-moving
    /// table.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows updated by one statement - matches <see cref="AccessRecordPruneJobOptions.BatchSize"/>'s
    /// own reasoning: no per-row external I/O, so the statement's own lock/WAL footprint is the only
    /// cost, and `operator_devices` is smaller than `access_records` by construction (one row per
    /// device, not per read).</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>A safety valve, not a target - the same role <see cref="OutboxPruneJobOptions.MaxBatchesPerCycle"/>
    /// plays for every sibling prune job: bounds one cycle's total work so a large first-run backlog
    /// cannot make it run indefinitely, leaving the rest for the next tick.</summary>
    public int MaxBatchesPerCycle { get; set; } = 50;
}
