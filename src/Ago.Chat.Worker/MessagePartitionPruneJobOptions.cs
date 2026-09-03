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
    /// value's role changed from "the one number everything prunes against" to <b>a ceiling nothing may
    /// exceed</b> - see <see cref="EffectiveHorizonMonths"/>. The disk-protection argument above is
    /// exactly why it must stay a ceiling rather than become a per-tier default a generous class could
    /// override: a business decision to grow one tier's window can never, by itself, grow how much
    /// `messages` a class is allowed to keep past this point - only a deliberate change to this value
    /// (an operator decision, not a pricing one) can do that. Still not a measurement - `15-05` supplies
    /// the real number (`adr/0031`), and this value is explicitly documented as what it replaces once
    /// that lands.</para></summary>
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
    /// <para><b>Paid tiers have no number of their own yet - stated, not invented.</b> `13-08`'s own
    /// brief: "if you cannot pick one honestly, say so and make the mechanism take a number per class
    /// with the paid ones left at today's behaviour." Only <c>"free"</c> has an entry; a class with no
    /// entry here falls back to <see cref="RetentionHorizonMonths"/> itself in
    /// <see cref="EffectiveHorizonMonths"/> - the exact behaviour every class had before this item, so
    /// starter/growth are provably unchanged rather than silently defaulted to some other number.</para></summary>
    public Dictionary<string, int> RetentionWindowMonthsByClass { get; set; } = new()
    {
        [RetentionClass.Free.Value] = 2,
    };

    /// <summary>The window a slice of <paramref name="retentionClass"/> actually prunes against - the
    /// one place `MessagePartitionPruneJob` and `MessageArchiveJob` both compute a horizon from, so they
    /// can never drift against each other's own reading of these two properties. <b>Always
    /// <c>Math.Min</c> of the class's configured window and <see cref="RetentionHorizonMonths"/></b>,
    /// never the configured window alone - <see cref="RetentionHorizonMonths"/>'s own remarks explain
    /// why the ceiling must win even when a class's own configured window is larger. A class with no
    /// entry in <see cref="RetentionWindowMonthsByClass"/> resolves to
    /// <see cref="RetentionHorizonMonths"/> itself, which the <c>Math.Min</c> then leaves unchanged -
    /// today's undifferentiated behaviour, exactly as before this item.</summary>
    public int EffectiveHorizonMonths(RetentionClass retentionClass)
    {
        var configured = RetentionWindowMonthsByClass.TryGetValue(retentionClass.Value, out var months)
            ? months
            : RetentionHorizonMonths;
        return Math.Min(configured, RetentionHorizonMonths);
    }

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
