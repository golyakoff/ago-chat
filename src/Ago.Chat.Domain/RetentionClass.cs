namespace Ago.Chat.Domain;

/// <summary>
/// `13-06`/`adr/0031`: the immutable half of a message's partition key. Stamped onto a
/// <see cref="Message"/> from its owning <see cref="Site"/>'s <see cref="Site.Tier"/> at the moment the
/// message is constructed (<see cref="Conversation.AddMessage"/> is the only place that ever produces
/// one), and never read back to decide anything about the tenant afterwards - the whole point of
/// `adr/0031`'s Decision 2 is that upgrading or downgrading a tenant moves no existing row between
/// partitions, because no row's own class is ever recomputed.
///
/// <para><b><see cref="FromTier"/> is the identity function today, and that is a deliberate, narrow
/// choice, not an oversight.</b> `13-01`/`13-02` closed <see cref="SubscriptionTierBands"/>'s tier set
/// at exactly three values - `"free"`, <see cref="SubscriptionTierBands.Starter"/>,
/// <see cref="SubscriptionTierBands.Growth"/> - which is also `adr/0031`'s own "three tiers, not
/// thousands" partition-count budget. Reusing the tier string as the class string needs no separate
/// mapping table to invent and drift out of sync with `SubscriptionTierBands`, and it costs nothing:
/// if a future pricing change ever needs several tiers to share one retention class (or one tier to
/// split across two), this factory method is the one place that changes - every caller, every
/// partition name and every archive manifest key already goes through it rather than reading
/// <see cref="Site.Tier"/> directly.</para>
///
/// <para>A plain string wrapper, not an enum, for the same reason <see cref="Site.Tier"/> itself is
/// not one (<see cref="Site.Tier"/>'s own remarks): the legal set is not fixed by this type, only by
/// whatever `SubscriptionTierBands` and `Site`'s own default currently allow, and a `text` column with
/// no enum this item would have to keep in lockstep is one fewer thing to guess wrong.</para>
/// </summary>
public readonly record struct RetentionClass(string Value)
{
    /// <summary>`Site.Tier`'s own default for every site that predates `13-02`'s billing, and the
    /// class a message gets when nothing else is known - see <see cref="FromTier"/>.</summary>
    public static readonly RetentionClass Free = new("free");

    public static RetentionClass FromTier(string tier) =>
        string.IsNullOrWhiteSpace(tier) ? Free : new RetentionClass(tier);

    /// <summary>Every retention class the platform recognises. Before `15-09`/`adr/0087`, this list
    /// also drove partition creation - `PartitionMaintenanceJob` read it to know which top-level `LIST`
    /// partitions had to exist, and `13-06`'s own migration read it to build the same three at
    /// migration time. `messages` no longer partitions by class at all (`adr/0087`: `retention_class`
    /// stays an ordinary column, replaced as the partition key by `HASH (site_id)`), so this list no
    /// longer has DDL consequences - it now serves only as the closed set retention logic (the prune
    /// sweep's own discovery query, the archive job) reasons about, and as a reference for tests. Still
    /// deliberately closed over `SubscriptionTierBands`' own tier set rather than discovered from
    /// `sites.tier` at runtime, for the identical reason `adr/0031` originally gave: a typo'd or
    /// since-retired tier string stamped onto an old row (there is no foreign key from
    /// `messages.retention_class` to a tier table - `Site.Tier` itself is `text`, not an enum) must
    /// never be read as a class this system is meant to recognise.</summary>
    public static readonly IReadOnlyList<RetentionClass> KnownClasses =
        [Free, new(SubscriptionTierBands.Starter), new(SubscriptionTierBands.Growth)];

    public override string ToString() => Value;
}
