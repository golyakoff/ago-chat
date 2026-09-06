using System.Collections.Concurrent;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Pipeline;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// `23-07`: <see cref="IWidgetActivityRecorder"/>'s own implementation - the accumulate half of
/// "accumulate and flush", on the shape <c>BatchAccumulator</c> establishes for messages, adapted for
/// counts rather than individual items. The difference from that type is the whole reason this is not
/// simply a second instance of it: a message batch holds each item until a worker has actually
/// written it and only then acks the caller (<c>WaitForFlushAsync</c>), because losing a message is
/// losing a conversation turn. A count has no such requirement - <see cref="RecordLoad"/>/
/// <see cref="RecordOpen"/>/<see cref="RecordConversation"/> return the instant the in-memory counter
/// is bumped, and <see cref="WidgetActivityFlusherService"/> drains this on its own timer with no
/// caller ever waiting on that drain (`docs/design/decisions.md` §3's own "a batched flush that loses
/// a few events on a pod restart is acceptable").
///
/// <para><b><see cref="DrainSnapshot"/> is an atomic swap, not a lock held across the flush.</b>
/// <see cref="Interlocked.Exchange{T}(ref T, T)"/> replaces the live dictionary with a fresh empty one
/// and hands the old one to the flusher - a <see cref="RecordLoad"/> call that read the old reference
/// a moment before the swap still lands in it, so that single increment is included in *this* flush
/// if it arrives before the flusher finishes enumerating, or the *next* flush if it arrives after
/// (the dictionary is still a live, writable object either way - nothing throws, nothing is lost
/// beyond the ordinary approximation this whole mechanism already accepts). The alternative - a lock
/// held for the duration of `FlushAsync`'s own network round trip - would make every visitor's beacon
/// wait behind whichever request happens to be flushing at that instant, on the single highest-volume
/// endpoint in the product (the item's own words).</para>
/// </summary>
public sealed class WidgetActivityAccumulator : IWidgetActivityRecorder
{
    private ConcurrentDictionary<(Guid SiteId, DateOnly Day), Counters> _current = new();

    public void RecordLoad(SiteId siteId, DateTimeOffset now) => Bump(siteId, now, c => Interlocked.Increment(ref c.Loads));

    public void RecordOpen(SiteId siteId, DateTimeOffset now) => Bump(siteId, now, c => Interlocked.Increment(ref c.Opens));

    public void RecordConversation(SiteId siteId, DateTimeOffset now) =>
        Bump(siteId, now, c => Interlocked.Increment(ref c.Conversations));

    private void Bump(SiteId siteId, DateTimeOffset now, Action<Counters> increment)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var counters = _current.GetOrAdd((siteId.Value, day), static _ => new Counters());
        increment(counters);
    }

    /// <summary>Called only by <see cref="WidgetActivityFlusherService"/>, on its own timer - swaps
    /// in a fresh dictionary and returns every delta the drained one held, as plain
    /// <see cref="WidgetActivityDelta"/> values with no reference back into this accumulator's own
    /// counters (this type's own doc comment explains why the swap itself is what makes this safe
    /// without a lock).</summary>
    internal IReadOnlyCollection<WidgetActivityDelta> DrainSnapshot()
    {
        var drained = Interlocked.Exchange(ref _current, new ConcurrentDictionary<(Guid, DateOnly), Counters>());
        if (drained.IsEmpty)
        {
            return [];
        }

        var deltas = new List<WidgetActivityDelta>(drained.Count);
        foreach (var ((siteId, day), counters) in drained)
        {
            deltas.Add(new WidgetActivityDelta(new SiteId(siteId), day, counters.Loads, counters.Opens, counters.Conversations));
        }

        return deltas;
    }

    /// <summary>A mutable holder so <see cref="Interlocked.Increment(ref int)"/> has a field to target -
    /// a <c>record struct</c> of three <see langword="int"/>s would need the whole tuple replaced
    /// under a lock on every single increment, which is exactly the per-beacon contention this
    /// accumulator exists to avoid.</summary>
    private sealed class Counters
    {
        public int Loads;
        public int Opens;
        public int Conversations;
    }
}
