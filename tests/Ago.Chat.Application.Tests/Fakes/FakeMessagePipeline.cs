using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records what was enqueued and hands back a configurable canned <see cref="Result{T}"/> -
/// stands in for the real queue-and-wait-for-a-worker's-ack behaviour, which
/// `Ago.Chat.Integration.Tests`/`Ago.Chat.Concurrency.Tests` prove against a real bounded channel and
/// real Postgres (testing.md: this level only needs to prove the handler enqueues the right thing and
/// forwards the pipeline's own result, not that a channel or a batch writer works).</summary>
public sealed class FakeMessagePipeline : IMessagePipeline
{
    private readonly List<PendingMessage> _enqueued = [];
    private readonly Result<int> _result;

    public FakeMessagePipeline(Result<int> result) => _result = result;

    public FakeMessagePipeline() : this(Result<int>.Success(1))
    {
    }

    public IReadOnlyList<PendingMessage> Enqueued => _enqueued;

    public Task<Result<int>> EnqueueAsync(PendingMessage message, CancellationToken cancellationToken)
    {
        _enqueued.Add(message);
        return Task.FromResult(_result);
    }
}
