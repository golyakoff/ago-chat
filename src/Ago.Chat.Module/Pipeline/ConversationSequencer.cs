using System.Collections.Concurrent;
using Ago.Chat.Domain;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// `4-05`: concurrency.md's "Per-conversation ordering under parallel consumers" - originally
/// described for the broker-consumer case (`3-02`), applied here to the in-process queue instead.
/// Guarantees two messages for the same <see cref="ConversationId"/> are never mid-flight in the
/// batch writer at once, even when dequeued by different <c>MessagePipelineWorkerHost</c> loops -
/// without this, two concurrent flushes could both load the same conversation, both read the same
/// <c>LastSequence</c>, and both compute the same next sequence number, which is exactly the
/// duplicate this whole item exists to prevent (`CLAUDE.md` rule 6: order is guaranteed per
/// conversation).
///
/// A ref-counted gate per conversation, not a bare <c>ConcurrentDictionary&lt;ConversationId,
/// SemaphoreSlim&gt;</c> with opportunistic removal-on-release: removing an entry the instant its
/// semaphore is released races a new caller who is concurrently acquiring that same instance via
/// <c>GetOrAdd</c> - a third caller arriving in that window would create and acquire a *different*
/// semaphore for the same conversation, running concurrently with the second caller and defeating
/// the whole guarantee. The ref count makes "safe to remove" exact: only when the last holder
/// releases and nothing is waiting. Entries are removed the moment they idle, not on a timer, so the
/// dictionary never grows unbounded (`concurrency.md`'s own rule: "a Dictionary that only grows is a
/// bug even if it works" - here that is nothing more than "an active conversation's entry stays,
/// an idle one is gone").
/// </summary>
public sealed class ConversationSequencer
{
    private readonly ConcurrentDictionary<ConversationId, Gate> _gates = new();

    public async Task RunAsync(ConversationId conversationId, Func<Task> action, CancellationToken cancellationToken)
    {
        var gate = Acquire(conversationId);
        try
        {
            await gate.Semaphore.WaitAsync(cancellationToken);
            try
            {
                await action();
            }
            finally
            {
                gate.Semaphore.Release();
            }
        }
        finally
        {
            Release(conversationId, gate);
        }
    }

    private Gate Acquire(ConversationId conversationId)
    {
        // The bookkeeping lock is only ever held across dictionary/refcount operations, never
        // across the semaphore wait or the action itself - concurrency.md: "no lock around await".
        lock (_gates)
        {
            var gate = _gates.GetOrAdd(conversationId, static _ => new Gate());
            gate.RefCount++;
            return gate;
        }
    }

    private void Release(ConversationId conversationId, Gate gate)
    {
        lock (_gates)
        {
            gate.RefCount--;
            if (gate.RefCount == 0)
            {
                _gates.TryRemove(conversationId, out _);
            }
        }
    }

    private sealed class Gate
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int RefCount;
    }
}
