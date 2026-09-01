using System.Text.RegularExpressions;

namespace Ago.Chat.Domain;

/// <summary>
/// `15-09`/`adr/0087`: the one place the `messages_{bucket:00}` naming scheme is spelled out, shared by
/// every project that needs to build one of <c>messages</c>' 64 hash-partition bucket names -
/// <c>Ago.Chat.Infrastructure.Postgres</c>'s repartitioning migration (builds the names once, at
/// migration time) and <c>Ago.Chat.Worker</c>'s retention sweep/archive jobs (build them every cycle, to
/// iterate the fixed bucket list). Lives in <c>Ago.Chat.Domain</c> - not <c>Ago.Chat.Worker</c>, which
/// the migration project cannot reference (a host-only project; <c>CLAUDE.md</c> rule 1's dependency
/// rule runs the other way) - so both sides build the *identical* string from the same code rather than
/// two hand-written interpolations that could drift.
///
/// <para><b>The bucket API is the live scheme; the class/month API below it is frozen history, kept
/// only because a migration that already shipped calls it.</b> Before `15-09`, a partition's name
/// encoded its own (retention class, month) - a fact <see cref="TryParse"/> used to recover from
/// `pg_partition_tree`'s live catalog read, because which partitions existed was itself dynamic
/// (created going forward by the now-deleted <c>PartitionMaintenanceJob</c>, one class-month at a
/// time). <c>CLAUDE.md</c>'s own rule - "never edit a migration that has been applied anywhere but the
/// local machine" - means `Stage13RepartitionMessagesByRetentionClass.cs` cannot be touched to stop
/// calling <see cref="ForClass(RetentionClass)"/>/<see cref="ForMonth"/>, even though nothing in current
/// runtime code calls them any more after this item's own migration
/// (<c>Stage15RepartitionMessagesByTenantHash</c>) superseded the scheme they built. They stay,
/// unchanged, for that one frozen caller alone.</para>
///
/// <para>Under `PARTITION BY HASH (site_id)` the bucket list is a compile-time constant -
/// <see cref="BucketCount"/> buckets, numbered <c>0</c> to <c>63</c>, created once by
/// `Stage15RepartitionMessagesByTenantHash` and never again. Nothing needs to ask Postgres which
/// partitions exist any more; every caller that used to enumerate `pg_partition_tree` now enumerates
/// <see cref="AllBucketNames"/> instead, a plain in-memory range.</para>
/// </summary>
public static partial class MessagePartitionNames
{
    /// <summary>`adr/0087`'s own number - fixed at creation, not changeable afterward without a full
    /// rehash-and-copy. A power of two so a later shard split divides cleanly (64 -> 32 -> 16 -> ...).
    /// Not a measurement (there is no traffic yet to size against, `CLAUDE.md` rule 7) - justified by
    /// splittability and by keeping the fan-out cost of a query that forgets a `site_id` predicate
    /// bounded, per the ADR's own Decision section.</summary>
    public const int BucketCount = 64;

    public static string ForBucket(int bucket)
    {
        if (bucket < 0 || bucket >= BucketCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucket), bucket, $"Bucket must be in [0, {BucketCount}).");
        }

        return $"messages_{bucket:D2}";
    }

    /// <summary>Every leaf partition name, in bucket order - the fixed, complete list every job that
    /// used to read `pg_partition_tree` now iterates instead. No database round trip: the list is
    /// exactly as knowable at compile time as `RetentionClass.KnownClasses` was for the scheme this
    /// one replaces.</summary>
    public static IReadOnlyList<string> AllBucketNames { get; } =
        [.. Enumerable.Range(0, BucketCount).Select(ForBucket)];

    // --- Frozen history below: the messages_<class>[_<yyyy>_<mm>] scheme, called only by
    // Stage13RepartitionMessagesByRetentionClass.cs (an applied migration - CLAUDE.md forbids editing
    // it) and by MessagePartitioningTests' own historical-schema assertions. Never called by anything
    // that reasons about the live schema after 15-09. Do not extend; do not call from new code.

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    /// <summary>Frozen history - see this type's own remarks. Only
    /// `Stage13RepartitionMessagesByRetentionClass.cs` still calls this.</summary>
    public static string ForClass(RetentionClass retentionClass)
    {
        if (!SafeIdentifier().IsMatch(retentionClass.Value))
        {
            throw new ArgumentException(
                $"'{retentionClass.Value}' is not a safe partition-name fragment.", nameof(retentionClass));
        }

        return $"messages_{retentionClass.Value}";
    }

    /// <summary>Frozen history - see this type's own remarks. Only
    /// `Stage13RepartitionMessagesByRetentionClass.cs` still calls this.</summary>
    public static string ForMonth(RetentionClass retentionClass, DateTimeOffset monthStart) =>
        $"{ForClass(retentionClass)}_{monthStart:yyyy_MM}";
}
