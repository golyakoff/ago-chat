using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `4-05`: same purpose as `Ago.Chat.Integration.Tests.SynchronousMessagePipeline` - a synchronous
/// stand-in for the real Channel/BackgroundService pipeline, flushing each
/// <see cref="EnqueueAsync"/> call immediately as a batch of one through the real
/// <c>MessageBatchWriter</c>. Tests here that are about something other than the pipeline's own
/// concurrency behaviour (reconnect, rate limiting under load) use this instead of standing up the
/// full worker/flusher stack - `MessagePipelineTests` in this project is what exercises the real
/// pipeline's own concurrency guarantees directly.
/// </summary>
public sealed class SynchronousMessagePipeline(NpgsqlDataSource dataSource) : IMessagePipeline
{
    private readonly MessageBatchWriter _writer = new(
        dataSource, new SystemClock(), new UuidV7Generator(), NullLogger<MessageBatchWriter>.Instance);

    public async Task<Result<int>> EnqueueAsync(PendingMessage message, CancellationToken cancellationToken)
    {
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InboundMessage(message, ack);
        await _writer.FlushAsync([item], cancellationToken);
        return await ack.Task;
    }
}
