using Ago.Chat.Contracts;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// `4-05`: the "batch writer" half of concurrency.md's diagram, minus the actual Postgres write
/// itself (<see cref="MessageBatchWriter"/> does that) - this class is only the "up to N rows or T
/// milliseconds, whichever comes first" timing decision.
///
/// Reads <see cref="BatchAccumulator"/>'s own internal channel with no <c>stoppingToken</c>
/// involvement at all, by design: its only stop signal is that channel completing, which
/// <see cref="MessagePipelineWorkerHost"/> only does once every item ever written to it has already
/// been flushed (see that class's own remarks) - by the time this loop's <c>WaitToReadAsync</c>
/// returns false, there is genuinely nothing left to lose by not reacting to shutdown directly.
/// </summary>
public sealed class BatchFlusherService(
    BatchAccumulator accumulator,
    MessageBatchWriter writer,
    IClock clock,
    IOptions<MessagePipelineOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = accumulator.Reader;
        var buffer = new List<InboundMessage>();

        while (await reader.WaitToReadAsync(CancellationToken.None))
        {
            var deadline = clock.UtcNow + options.Value.BatchMaxDelay;
            while (buffer.Count < options.Value.BatchMaxRows)
            {
                if (reader.TryRead(out var item))
                {
                    buffer.Add(item);
                    continue;
                }

                var remaining = deadline - clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var readWait = reader.WaitToReadAsync(CancellationToken.None).AsTask();
                var delay = Task.Delay(remaining, CancellationToken.None);
                var winner = await Task.WhenAny(readWait, delay);
                if (winner == delay)
                {
                    break;
                }

                if (!await readWait)
                {
                    break; // Channel completed with nothing further to read.
                }
            }

            if (buffer.Count > 0)
            {
                // `7-02`: nfr.md's "batch size histogram" - recorded before the write itself, since
                // this is about how large the attempted batch was, independent of whether the flush
                // that follows succeeds.
                ChatMetrics.RecordBatchSize(buffer.Count);
                await writer.FlushAsync(buffer, CancellationToken.None);
                buffer.Clear();
            }
        }
    }
}
