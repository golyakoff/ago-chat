using Ago.Chat.Application.UseCases;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// `4-05`: the "N pipeline workers" half of concurrency.md's diagram. Runs
/// <see cref="MessagePipelineOptions.WorkerCount"/> concurrent loops, all reading the same
/// <see cref="ChannelMessagePipeline"/> channel (safe by design - <c>System.Threading.Channels</c>
/// supports multiple concurrent readers), each routing what it dequeues through
/// <see cref="ConversationSequencer"/> into <see cref="BatchAccumulator"/>.
///
/// Shutdown is deliberately *not* driven by <paramref name="stoppingToken"/> inside the loops - see
/// <see cref="ChannelMessagePipeline"/>'s constructor: it completes the inbound channel the instant
/// <c>ApplicationStopping</c> fires, which is the only signal these loops respond to
/// (`await foreach` over <c>ReadAllAsync</c> ends on its own once the channel is completed *and*
/// drained). This is concurrency.md's shutdown sequence - "drain the pipeline channel -&gt; flush the
/// batch writer" - made real for the first time; cancelling <paramref name="stoppingToken"/> instead
/// would abort mid-drain and lose whatever was still queued, exactly what that sequence exists to
/// avoid. <see cref="StopAsync"/> only adds a bound on how long this is allowed to take.
/// </summary>
public sealed class MessagePipelineWorkerHost(
    ChannelMessagePipeline pipeline,
    ConversationSequencer sequencer,
    BatchAccumulator accumulator,
    IOptions<MessagePipelineOptions> options,
    ILogger<MessagePipelineWorkerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = Enumerable.Range(0, options.Value.WorkerCount)
            .Select(_ => RunWorkerLoopAsync())
            .ToArray();
        await Task.WhenAll(loops);

        // Every loop above only returns once every item it ever dequeued has been written to the
        // accumulator *and* had its ack completed (BatchAccumulator.WaitForFlushAsync awaits the
        // ack, which only resolves once BatchFlusherService actually flushes it) - so nothing can
        // still be pending against the accumulator's channel at this point. Safe to complete it now;
        // this is what lets BatchFlusherService's own loop end gracefully in turn.
        accumulator.Complete();
    }

    private async Task RunWorkerLoopAsync()
    {
        var inFlight = new List<Task>();
        await foreach (var item in pipeline.Reader.ReadAllAsync(CancellationToken.None))
        {
            inFlight.Add(RunSequencedAsync(item));
            inFlight.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(inFlight);
    }

    private async Task RunSequencedAsync(InboundMessage item)
    {
        try
        {
            await sequencer.RunAsync(
                item.Message.ConversationId,
                () => accumulator.WaitForFlushAsync(item, CancellationToken.None),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // A supervised, tracked task (concurrency.md: "fire-and-forget is forbidden") - added to
            // RunWorkerLoopAsync's own inFlight list and awaited there, so a failure surfaces here
            // instead of becoming an UnobservedTaskException. The caller's ack still needs
            // completing: WaitForFlushAsync/MessageBatchWriter handle their own failure paths
            // internally, but a failure this far out (e.g. the accumulator's channel write itself
            // throwing) would otherwise leave the caller waiting forever.
            logger.LogError(ex, "Pipeline worker failed to sequence a message for conversation {ConversationId}.", item.Message.ConversationId);
            item.Ack.TrySetResult(ConversationErrors.Unavailable("Failed to process message, try again."));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(options.Value.ShutdownDrainTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        await base.StopAsync(linked.Token);
    }
}
