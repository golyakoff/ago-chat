namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-17`: turns a raw concurrent-load integer (`ConversationAssignmentOverlapQuery`'s own question,
/// "how many did this operator hold at instant T", applied per assignment interval by
/// <c>OperatorLoadReportReadStore</c>) into one of a small, configured set of buckets - a pure
/// function with no I/O, so <see cref="AnalyticsOptions.LoadBucketUpperBounds"/>'s own shape
/// ("buckets are configuration, not literals in SQL", the backlog item's own Scope) is testable with
/// no database at all. Kept in <c>Ago.Chat.Application</c> even though its only caller today is a
/// Postgres read store: the dependency rule (`clean-architecture.md`) says nothing about where a type
/// belongs is decided by who happens to call it first - a function over plain integers has no
/// Postgres-shaped reason to live in <c>Ago.Chat.Infrastructure.Postgres</c>, and a unit test here is
/// cheaper and faster than the integration test the alternative placement would force for a rule that
/// touches no row. The same "policy in Application, mechanism in Infrastructure" split
/// <c>PrecedingPeriod</c> already draws for the preceding-window computation.
/// </summary>
public static class OperatorLoadBuckets
{
    /// <summary>The bucket index for <paramref name="concurrentLoad"/> against ascending, positive
    /// <paramref name="upperBounds"/>: the index of the first bound the load does not exceed, or
    /// <c>upperBounds.Count</c> - one past the last real bucket - for the open-ended "more than the
    /// highest named bound" case. <see cref="AnalyticsOptions.LoadBucketUpperBounds"/>'s own
    /// <c>[1, 3, 5, 8]</c> default puts a load of 1 in bucket 0, a load of 2 or 3 in bucket 1, and a
    /// load of 9 or more in bucket 4 (the open-ended one).</summary>
    public static int IndexOf(IReadOnlyList<int> upperBounds, int concurrentLoad)
    {
        for (var i = 0; i < upperBounds.Count; i++)
        {
            if (concurrentLoad <= upperBounds[i])
            {
                return i;
            }
        }

        return upperBounds.Count;
    }

    /// <summary>The human-readable label for bucket <paramref name="index"/> - <c>"1"</c> for a
    /// single-wide bucket, <c>"2-3"</c> for a real range, <c>"9+"</c> for the open-ended last bucket.
    /// Pure arithmetic over the same <paramref name="upperBounds"/> <see cref="IndexOf"/> reads, so a
    /// label printed on a report always agrees with the bucket that produced it - there is no second
    /// place the two could drift apart from each other.</summary>
    public static string Label(IReadOnlyList<int> upperBounds, int index)
    {
        var lowerBound = index == 0 ? 1 : upperBounds[index - 1] + 1;
        if (index == upperBounds.Count)
        {
            return $"{lowerBound}+";
        }

        var upperBound = upperBounds[index];
        return lowerBound == upperBound ? $"{lowerBound}" : $"{lowerBound}-{upperBound}";
    }

    /// <summary>Validates the shape <see cref="IndexOf"/>/<see cref="Label"/> both assume: at least one
    /// bound, every bound positive, strictly ascending. Called from
    /// <c>Ago.Chat.Module.ChatModule</c>'s own <c>AddOptions&lt;AnalyticsOptions&gt;().Validate(...)</c>
    /// chain - the same "fail the pod at boot, not the first report" shape
    /// <see cref="AnalyticsOptions.MinimumSampleForRate"/>'s own validation already uses.</summary>
    public static bool IsValidConfiguration(IReadOnlyList<int> upperBounds)
    {
        if (upperBounds.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < upperBounds.Count; i++)
        {
            if (upperBounds[i] < 1)
            {
                return false;
            }

            if (i > 0 && upperBounds[i] <= upperBounds[i - 1])
            {
                return false;
            }
        }

        return true;
    }
}
