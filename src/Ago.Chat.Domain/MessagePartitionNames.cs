using System.Text.RegularExpressions;

namespace Ago.Chat.Domain;

/// <summary>
/// `13-06`/`adr/0031`: the one place the `messages_{class}[_{yyyy}_{mm}]` naming scheme is spelled out,
/// shared by every project that needs to build or parse a partition name - `Ago.Chat.Infrastructure.
/// Postgres`'s repartitioning migration (builds names) and `Ago.Chat.Worker`'s
/// `PartitionMaintenanceJob`/`MessagePartitionPruneQuery`/`MessageSearchIndexJob`/
/// `MessageSiteIdBackfillJob`/`MessageArchiveJob` (build and parse them) alike. Lives in
/// <c>Ago.Chat.Domain</c> - not <c>Ago.Chat.Worker</c>, which the migration project cannot reference
/// (a host-only project; <c>CLAUDE.md</c> rule 1's dependency rule runs the other way, Infrastructure
/// depending on Domain, never a host) - so both sides build the *identical* string from the same
/// code rather than two hand-written interpolations that could drift.
///
/// <para>Class names are never attacker- or tenant-controlled: they only ever come from
/// <see cref="RetentionClass.KnownClasses"/>, a fixed, code-owned list (<see cref="Site.Tier"/> is
/// free-form `text`, but nothing here interpolates a raw `Tier` value into SQL - every partition-DDL
/// caller goes through <see cref="RetentionClass.KnownClasses"/> instead). <see cref="ForClass"/>
/// still asserts the identifier is safe before building a name from it, the same defence-in-depth
/// <see cref="TryParse"/>'s own pattern match already applies on the read side - belt and braces, not
/// load-bearing on its own.</para>
/// </summary>
public static partial class MessagePartitionNames
{
    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex SafeIdentifier();

    // messages_<class>_<yyyy>_<mm> - class first (greedy, since it may itself contain digits and
    // underscores) then the trailing year/month this codebase's monthly partitions always end in.
    [GeneratedRegex(@"^messages_(?<class>[a-z][a-z0-9_]*)_(?<year>\d{4})_(?<month>\d{2})$")]
    private static partial Regex MonthlyPartitionPattern();

    public static string ForClass(RetentionClass retentionClass)
    {
        if (!SafeIdentifier().IsMatch(retentionClass.Value))
        {
            throw new ArgumentException(
                $"'{retentionClass.Value}' is not a safe partition-name fragment.", nameof(retentionClass));
        }

        return $"messages_{retentionClass.Value}";
    }

    public static string ForMonth(RetentionClass retentionClass, DateTimeOffset monthStart) =>
        $"{ForClass(retentionClass)}_{monthStart:yyyy_MM}";

    /// <summary>Parses a leaf (monthly) partition's name back into its class and period - the
    /// counterpart <see cref="ForMonth"/> read-side callers (<c>MessagePartitionPruneQuery</c>) need.
    /// <see langword="false"/> for anything that is not one of this scheme's own monthly leaves,
    /// including a class-level (mid-tree) partition name like <c>messages_free</c> - a caller
    /// enumerating leaves via <c>pg_partition_tree</c>'s own <c>isleaf</c> flag should never see one of
    /// those, but this stays a safe no-match rather than a throw for anything unexpected it is handed
    /// regardless.</summary>
    public static bool TryParse(string partitionName, out RetentionClass retentionClass, out DateOnly periodStart)
    {
        var match = MonthlyPartitionPattern().Match(partitionName);
        if (!match.Success)
        {
            retentionClass = default;
            periodStart = default;
            return false;
        }

        retentionClass = new RetentionClass(match.Groups["class"].Value);
        periodStart = new DateOnly(int.Parse(match.Groups["year"].Value), int.Parse(match.Groups["month"].Value), 1);
        return true;
    }
}
