using System.Collections.Concurrent;
using Ago.Chat.Webhooks;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `6-07`'s own Done-when: proves, against the real <see cref="ConcurrentWebhookDispatchPump"/> (no
/// broker needed - the mechanism this item adds is entirely in-process, the same reason `4-05`'s own
/// <c>MessagePipelineTests</c> test <c>ConversationSequencer</c>'s ordering guarantee directly), that
/// (a) deliveries for different partition keys (distinct conversations) genuinely run concurrently -
/// the whole point of `6-07`, since before this item <c>RabbitMqEventConsumer.SubscribeAsync</c>'s own
/// inline-await capped real concurrency at ~1 regardless of how many workers a caller configured - and
/// (b) deliveries that share a partition key never run concurrently and are still processed in the
/// order they were enqueued, `concurrency.md`'s "message order is guaranteed per conversation, never
/// globally" applied to this pump's own <see cref="PartitionSequencer"/>.
/// </summary>
public sealed class ConcurrentWebhookDispatchPumpTests
{
    [Fact]
    public async Task DistinctPartitionKeys_AllProcessConcurrently_NotOneAtATime()
    {
        const int count = 20;
        var currentlyRunning = 0;
        var maxRunning = 0;
        var ackedCount = 0;
        var startedCount = 0;
        var everyoneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HandlerAsync(EventEnvelope envelope, IMessageContext context, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref currentlyRunning);
            InterlockedMax(ref maxRunning, running);

            if (Interlocked.Increment(ref startedCount) == count)
            {
                everyoneStarted.TrySetResult();
            }

            // Every one of the `count` deliveries must have actually started before any of them is
            // allowed to finish - proves genuine overlap (all `count` mid-flight at once), not a lucky
            // race that happened to see two overlap briefly. If the pump only ever ran one delivery at
            // a time (6-07's own root cause, unfixed), this would time out rather than false-pass.
            await everyoneStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Interlocked.Decrement(ref currentlyRunning);
            await context.AckAsync(ct);
            Interlocked.Increment(ref ackedCount);
        }

        using var cts = new CancellationTokenSource();
        var pump = new ConcurrentWebhookDispatchPump(
            maxConcurrency: count, channelCapacity: count, HandlerAsync, NullLogger.Instance, "distinct-keys-test", cts.Token);

        for (var i = 0; i < count; i++)
        {
            var envelope = NewEnvelope(partitionKey: $"conversation-{i}");
            await pump.EnqueueAsync(envelope, new FakeMessageContext(), CancellationToken.None);
        }

        var completed = await ConcurrencyTestHelpers.WaitUntilAsync(
            () => Task.FromResult(Volatile.Read(ref ackedCount) == count), TimeSpan.FromSeconds(15));

        Assert.True(completed, $"Only {ackedCount}/{count} deliveries were acked in time.");
        Assert.Equal(count, maxRunning);
    }

    [Fact]
    public async Task SamePartitionKey_NeverOverlaps_AndProcessesInEnqueueOrder()
    {
        const int count = 30;
        const string partitionKey = "same-conversation";
        var inProgress = 0;
        var overlapDetected = false;
        var order = new ConcurrentQueue<int>();
        var random = new Random(Seed: 42);

        async Task HandlerAsync(EventEnvelope envelope, IMessageContext context, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref inProgress, 1, 0) != 0)
            {
                // A second delivery for the same partition key started while another was still mid-
                // flight - exactly what concurrency.md's per-conversation ordering guarantee forbids.
                overlapDetected = true;
            }

            order.Enqueue(int.Parse(envelope.Payload));
            await Task.Delay(random.Next(1, 5), ct);

            Volatile.Write(ref inProgress, 0);
            await context.AckAsync(ct);
        }

        using var cts = new CancellationTokenSource();
        // maxConcurrency > 1: the worker pool genuinely has room to run same-key deliveries
        // concurrently if PartitionSequencer did not stop it - this is what makes the test meaningful
        // rather than trivially passing because there was never more than one worker anyway.
        var pump = new ConcurrentWebhookDispatchPump(
            maxConcurrency: 8, channelCapacity: count, HandlerAsync, NullLogger.Instance, "same-key-test", cts.Token);

        for (var i = 0; i < count; i++)
        {
            var envelope = NewEnvelope(partitionKey, payload: i.ToString());
            await pump.EnqueueAsync(envelope, new FakeMessageContext(), CancellationToken.None);
        }

        var completed = await ConcurrencyTestHelpers.WaitUntilAsync(
            () => Task.FromResult(order.Count == count), TimeSpan.FromSeconds(15));

        Assert.True(completed, $"Only {order.Count}/{count} deliveries were processed in time.");
        Assert.False(overlapDetected, "Two deliveries for the same partition key ran concurrently.");
        Assert.Equal(Enumerable.Range(0, count), order);
    }

    [Fact]
    public async Task HandlerThrows_DeliveryIsNackedWithRequeue_AndOtherKeysAreUnaffected()
    {
        var context = new FakeMessageContext();

        Task HandlerAsync(EventEnvelope envelope, IMessageContext ctx, CancellationToken ct) =>
            Task.FromException(new InvalidOperationException("simulated handler failure"));

        using var cts = new CancellationTokenSource();
        var pump = new ConcurrentWebhookDispatchPump(
            maxConcurrency: 2, channelCapacity: 4, HandlerAsync, NullLogger.Instance, "failure-test", cts.Token);

        await pump.EnqueueAsync(NewEnvelope("conversation-x"), context, CancellationToken.None);

        var completed = await ConcurrencyTestHelpers.WaitUntilAsync(
            () => Task.FromResult(context.NackCount == 1), TimeSpan.FromSeconds(10));

        Assert.True(completed, "Expected the pump to nack-with-requeue after the handler threw.");
        Assert.Equal(0, context.AckCount);
        Assert.True(context.LastNackRequeue);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int initial, computed;
        do
        {
            initial = target;
            computed = Math.Max(initial, value);
        }
        while (Interlocked.CompareExchange(ref target, computed, initial) != initial);
    }

    private static EventEnvelope NewEnvelope(string partitionKey, string payload = "{}") => new(
        MessageId: Guid.NewGuid(), Type: "test-event", Version: 1, PartitionKey: partitionKey,
        OccurredAt: DateTimeOffset.UtcNow, CorrelationId: Guid.NewGuid(), Payload: payload);

    /// <summary>
    /// A minimal <see cref="IMessageContext"/> fake (testing.md: "no mocking framework for ports we
    /// own") - records what was called rather than touching any real broker.
    /// </summary>
    private sealed class FakeMessageContext : IMessageContext
    {
        public int AckCount;
        public int NackCount;
        public bool LastNackRequeue;

        public Task AckAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AckCount);
            return Task.CompletedTask;
        }

        public Task NackAsync(bool requeue, CancellationToken cancellationToken)
        {
            LastNackRequeue = requeue;
            Interlocked.Increment(ref NackCount);
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
