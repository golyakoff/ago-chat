using System.Threading.Channels;
using Ago.Chat.Infrastructure.Postgres.Pipeline;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// The handoff between <c>MessagePipelineWorkerHost</c> (which has already sequenced an item through
/// <see cref="ConversationSequencer"/>) and <c>BatchFlusherService</c> (which decides when a batch is
/// full enough, or old enough, to flush). Unbounded, safely - backpressure is already enforced
/// upstream by <c>ChannelMessagePipeline</c>'s bounded channel, so this can never hold more than
/// that channel's own capacity plus however many workers are concurrently mid-flight.
///
/// <see cref="WaitForFlushAsync"/> is the whole reason this exists as its own type instead of a bare
/// channel: it does not return once the item is queued, it returns once the item's batch has
/// actually flushed - which is what lets <see cref="ConversationSequencer.RunAsync"/> hold a
/// conversation's gate for the item's *entire* round trip, not just until it is handed off.
/// </summary>
public sealed class BatchAccumulator
{
    private readonly Channel<InboundMessage> _channel = Channel.CreateUnbounded<InboundMessage>();

    internal async Task WaitForFlushAsync(InboundMessage item, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(item, cancellationToken);
        await item.Ack.Task.WaitAsync(cancellationToken);
    }

    internal ChannelReader<InboundMessage> Reader => _channel.Reader;

    /// <summary>Only safe to call once every writer is done - see
    /// <c>MessagePipelineWorkerHost.ExecuteAsync</c>'s own remarks on why that is guaranteed by
    /// construction, not by a separate coordination step.</summary>
    internal void Complete() => _channel.Writer.TryComplete();
}
