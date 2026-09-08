using System.ComponentModel.DataAnnotations;
using Ago.Chat.Domain;

namespace Ago.Chat.Worker;

/// <summary>Bound from <c>MessagePartitionPruneJob:*</c> config keys. Uses
/// <c>ValidateDataAnnotations</c> (matching <c>DemoTenantExpiryJobOptions</c>'s own precedent for a
/// group that needs a real range check, not just presence) rather than plain <c>ValidateOnStart</c> -
/// <see cref="RetentionHorizonMonths"/>'s ceiling is a correctness guarantee, not a preference. `13-08`
/// adds <see cref="RetentionWindowMonthsByClass"/>/<see cref="EffectiveHorizonMonths"/> alongside it, a
/// second guarantee (<see cref="RetentionWindowMonthsByClass"/>'s own values are never negative or zero)
/// that `ValidateDataAnnotations` cannot express against a dictionary's values - enforced instead by an
/// explicit <c>.Validate(...)</c> call registered next to this type's own options binding in
/// <c>Program.cs</c>.</summary>
public sealed class MessagePartitionPruneJobOptions
{
    public const string SectionName = "MessagePartitionPruneJob";

    /// <summary>Daily, matching <see cref="PartitionMaintenanceJobOptions.Interval"/> - the natural
    /// pairing: partitions change on a monthly boundary, so a check more often than daily buys
    /// nothing, and this job's own <c>DROP</c> decision is exactly as time-insensitive as that job's
    /// <c>CREATE</c> decision already is.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>`15-04`'s scope: "the horizon here is an operational default, chosen so the disk
    /// survives... configurable rather than baked into a migration." Three months is chosen for three
    /// reasons stated together because none alone would be enough: (1) it keeps `messages` - the
    /// largest table in the system and the one `2-06` partitioned specifically so old data could be
    /// dropped cheaply - to at most four or five live monthly partitions at a time (the current month,
    /// `PartitionMaintenanceJobOptions.MonthsAhead` = 2 future months always kept ready, plus up to
    /// three trailing months not yet past this horizon), bounding total index size the way `2-06`'s own
    /// rationale asks for; (2) it is deliberately shorter than any plausible real product retention
    /// window `13-05` might eventually decide - an operational default protecting a 2Gi disk should
    /// default toward safety, not presume a generous policy nobody has approved, and raising a config
    /// value later is cheap while a full disk is an outage now; (3) it is comfortably longer than every
    /// other operational cadence already in this system (`15-03`'s 24h alert repeat, `OutboxPruneJob`'s
    /// 24h window), so nobody's routine incident response is ever racing this horizon.
    ///
    /// <para>`13-08`: since a per-tier window exists (<see cref="RetentionWindowMonthsByClass"/>), this
    /// value's role changed from "the one number everything prunes against" to <b>a ceiling an
    /// explicitly configured window may never exceed</b> - see <see cref="EffectiveHorizonMonths"/>. The
    /// disk-protection argument above is exactly why a class that names a finite window still cannot push
    /// past it by configuration mistake: a large number typed into
    /// <see cref="RetentionWindowMonthsByClass"/> by accident can never, by itself, grow how much
    /// `messages` that class is allowed to keep past this point - only a deliberate change to this value
    /// (an operator decision) can do that.
    ///
    /// <para><b>`23-74`/`ago-business/0012` §5 changed what "no entry at all" means, and this ceiling no
    /// longer reaches that case.</b> Before `23-74`, a class absent from
    /// <see cref="RetentionWindowMonthsByClass"/> fell back to this ceiling - "today's undifferentiated
    /// behaviour" was `13-08`'s own phrase for it, because no tier had yet been promised anything longer.
    /// The tier grid (2026-09-07) now promises Business "история переписки и файлы - вечно... пока тариф
    /// оплачивается" (forever, while paid) - a promise this ceiling would silently break if "no entry"
    /// still meant "three months like everyone else." §5 also names the reason this is safe rather than a
    /// re-run of the same disk risk this value was written to prevent: "forever" is guarded by a
    /// **storage quota per tenant** (Solo 100MB, Business 1GB per paid year - `23-52`/`23-80`/`23-82`, not
    /// this item's scope), not by a time window. Time and volume are "разные вещи" (different things) in
    /// the decision's own words, and this ceiling only ever protected against unbounded *time*. See
    /// <see cref="EffectiveHorizonMonths"/> for the resulting rule.</para></summary>
    [Range(1, int.MaxValue, ErrorMessage = "RetentionHorizonMonths must be at least 1 - a horizon of 0 or less would make the most recently completed month a drop candidate immediately, leaving no trailing month ever fully settled before eligibility.")]
    public int RetentionHorizonMonths { get; set; } = 3;

    /// <summary>`13-08`: the free tier's own two-month retention window, matching the decision this item
    /// implements - "the free tier is two operators with two months of history." Keyed by
    /// <see cref="RetentionClass.Value"/> (<c>"free"</c>/<see cref="SubscriptionTierBands.Starter"/>/
    /// <see cref="SubscriptionTierBands.Growth"/>), not a fixed three-field record - `RetentionClass`'s
    /// own set is already closed over `SubscriptionTierBands` elsewhere in this codebase, and a
    /// dictionary keyed the same way costs nothing extra while leaving room for a class this file does
    /// not have to be edited to add.
    ///
    /// <para><b>`23-74`: an absent entry now means "no time window at all" - forever, while the tier
    /// stays paid - not "fall back to the ceiling."</b> `13-08` left starter/growth unconfigured because
    /// "paid tiers have no number of their own yet"; `ago-business/0012` (2026-09-07) gave them one: no
    /// number, on purpose, for as long as the tenant pays. Only <c>"free"</c> has an entry for exactly
    /// that reason - it is the one class with an actual finite window to name. See
    /// <see cref="EffectiveHorizonMonths"/> for how an absent entry is read.</para></summary>
    public Dictionary<string, int> RetentionWindowMonthsByClass { get; set; } = new()
    {
        [RetentionClass.Free.Value] = 2,
    };

    /// <summary>The window a slice of <paramref name="retentionClass"/> actually prunes against, or
    /// <see langword="null"/> if that class has no time-based expiry at all - the one place
    /// `MessagePartitionPruneJob` and `MessageArchiveJob` both compute a horizon from, so they can never
    /// drift against each other's own reading of these two properties.
    ///
    /// <para><b>A class present in <see cref="RetentionWindowMonthsByClass"/></b> gets
    /// <c>Math.Min</c> of its configured window and <see cref="RetentionHorizonMonths"/>, never the
    /// configured window alone - <see cref="RetentionHorizonMonths"/>'s own remarks explain why the
    /// ceiling still wins over a configuration mistake even here.</para>
    ///
    /// <para><b>A class absent from <see cref="RetentionWindowMonthsByClass"/></b> returns
    /// <see langword="null"/> - `23-74`: no cutoff is ever computed for it, so `MessagePartitionPruneQuery`'s
    /// own join ("a class that has no cutoff of its own simply is not in the map and never matches the
    /// join") never selects a single one of its rows. This is what makes starter/growth's "forever, while
    /// paid" true by construction rather than by a very large configured number - a number would still be
    /// silently `Math.Min`-capped by the ceiling above; the absence of one is not.</para></summary>
    public int? EffectiveHorizonMonths(RetentionClass retentionClass) =>
        RetentionWindowMonthsByClass.TryGetValue(retentionClass.Value, out var months)
            ? Math.Min(months, RetentionHorizonMonths)
            : null;

    /// <summary>`15-09`/`adr/0087`: the removal mechanism changed from `DROP PARTITION` (one statement,
    /// instant, whole-partition) to `DELETE ... WHERE` (row-by-row, `adr/0087`'s own accepted
    /// regression - "slower, generates more WAL, marks rows dead rather than reclaiming space"). A
    /// confirmed-archived (site, class, period) slice can hold an unbounded number of rows, so the
    /// delete is a bounded, `FOR UPDATE SKIP LOCKED` loop - the same shape
    /// `ConversationErasureQuery.DeleteMessageBatchAsync`'s own per-conversation loop already
    /// establishes - rather than one unbounded statement holding a lock across however many rows one
    /// tenant's one expired month happens to have.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "DeleteBatchSize must be at least 1.")]
    public int DeleteBatchSize { get; set; } = 500;
}
