using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-05`: a synchronous stand-in for the real Channel/BackgroundService pipeline - each
/// <see cref="EnqueueAsync"/> call flushes immediately, as a batch of one, through the real
/// <c>MessageBatchWriter</c> (real Postgres, real EF, real outbox - nothing about persistence
/// correctness is faked here, only the queueing/batching that `MessagePipelineWorkerHost`/
/// `BatchFlusherService` add on top). Tests about something other than the pipeline's own batching,
/// backpressure, or per-conversation sequencing (fanout, outbox dispatch, rate limiting, reconnect)
/// use this instead of standing up the full worker/flusher stack for every send -
/// `MessageBatchWriter.FlushAsync` stays internal to `Ago.Chat.Infrastructure.Postgres`, reached
/// here via that project's own `InternalsVisibleTo`, the same precedent it already established for
/// this test project (RBAC storage internals) and `Ago.Chat.Worker` reused for its own tick tests.
/// </summary>
public sealed class SynchronousMessagePipeline(NpgsqlDataSource dataSource) : IMessagePipeline
{
    private readonly MessageBatchWriter _writer = new(
        dataSource, new SystemClock(), new UuidV7Generator(), NullLogger<MessageBatchWriter>.Instance);

    public async Task<Result<int>> EnqueueAsync(PendingMessage message, CancellationToken cancellationToken)
    {
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InboundMessage(message, ack, Stopwatch.GetTimestamp());
        await _writer.FlushAsync([item], cancellationToken);
        return await ack.Task;
    }
}
