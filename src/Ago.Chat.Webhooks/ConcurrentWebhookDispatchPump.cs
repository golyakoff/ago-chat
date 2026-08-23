using System.Collections.Concurrent;
using System.Threading.Channels;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Webhooks;

/// <summary>
/// `6-07`: concurrency.md's "In-process pipeline" shape (`4-05`'s <c>ChannelMessagePipeline</c>/
/// <c>MessagePipelineWorkerHost</c>/<c>ConversationSequencer</c> trio), rebuilt here rather than
/// reused from <c>Ago.Chat.Module</c> - `4-05`'s version is keyed on the strongly-typed
/// <c>ConversationId</c> and lives beside <c>SendVisitorMessageHandler</c>'s own write path, entangled
/// with the message-send domain; this one is keyed on the broker's own
/// <see cref="EventEnvelope.PartitionKey"/> (already the conversation id at the wire level -
/// <c>RabbitMqEventPublisher</c> publishes with <c>routingKey = envelope.PartitionKey</c>, echoed back
/// unchanged as <c>delivery.RoutingKey</c> on receipt) and exists purely to compensate for
/// <c>Ago.Platform.Messaging.RabbitMq.RabbitMqEventConsumer.SubscribeAsync</c>'s own inline-await
/// dispatch (`docs/backlog/6-07`'s own root cause, found live by `6-06`'s load-proof run: at most one
/// delivery processed at a time per subscription, regardless of <c>PrefetchCount</c>).
///
/// `6-07`'s own Scope named two shapes: fix the platform's <c>RabbitMqEventConsumer</c> itself, or -
/// "if narrower is cleaner" - fix only the two webhook-dispatch consumer registrations. This is the
/// narrower shape: no `ago-platform` change, no package version bump, no risk to every other
/// <c>Competing</c> consumer in the codebase (`6-07`'s own Out of scope explicitly excludes touching
/// them) - the compensation lives entirely in the one host (`Ago.Chat.Webhooks`) that measured the gap.
///
/// The delegate registered with <c>IEventConsumer.SubscribeAsync</c> by this pump's own caller
/// (<see cref="ConversationAssignmentWebhookDispatchConsumer"/>/
/// <see cref="ConversationClosedWebhookDispatchConsumer"/>) does almost no work itself - it only wraps
/// the delivery's <see cref="IMessageContext"/> (see <see cref="GatedMessageContext"/> below) and
/// writes it onto <see cref="_queue"/>, then returns. That is what lets
/// <c>RabbitMqEventConsumer</c>'s own inline <c>await handler(...)</c> complete almost immediately, so
/// the broker's client library moves on and dispatches the *next* delivery right away instead of
/// waiting for this one's real processing to finish - the actual handler work (an outbound HTTP call
/// that may hang for seconds) happens under <see cref="_sequencer"/>'s own bounded concurrency instead,
/// up to <c>maxConcurrency</c> deliveries at once. The bounded channel (`concurrency.md`: "every
/// in-process queue is a bounded <c>Channel&lt;T&gt;</c>... a full channel means backpressure, never
/// unbounded growth") sits independently of the broker's own <c>PrefetchCount</c> - a delivery still
/// counts against prefetch (unacked) for as long as it sits queued or in flight here, so the two
/// bounds compose rather than substitute for each other.
///
/// `concurrency.md`'s ordering guarantee ("message order is guaranteed per conversation, never
/// globally") must survive whatever concurrency this adds - and must survive it *exactly*, not just
/// "no two deliveries for the same key ever overlap": a first design here reused `4-05`'s own
/// <c>ConversationSequencer</c> shape verbatim (a ref-counted <c>SemaphoreSlim</c> per key) and a
/// direct test of it (<c>ConcurrentWebhookDispatchPumpTests</c>) caught a real bug that shape hides -
/// <see cref="PartitionSequencer"/>'s own remarks explain why mutual exclusion alone is not enough
/// here, unlike for `4-05`'s own use of the same shape.
///
/// RabbitMQ.Client's own guidance (`.NET/C# client API guide`): a channel must not be used
/// concurrently from multiple threads without external synchronization - "sharing a channel for
/// concurrent publishing will lead to incorrect frame interleaving at the protocol level." Every
/// delivery on one subscription shares the same underlying <c>IChannel</c> (one created per
/// <c>SubscribeAsync</c> call), so <see cref="GatedMessageContext"/> serializes every
/// Ack/Nack/DeadLetter call this pump ever makes through one <see cref="SemaphoreSlim"/> per pump
/// instance - cheap, since ack/nack/publish are fast calls, unlike the handler work they gate, which
/// runs fully concurrently across partition keys.
/// </summary>
internal sealed class ConcurrentWebhookDispatchPump
{
    private readonly Channel<QueuedDelivery> _queue;
    private readonly SemaphoreSlim _channelGate = new(1, 1);
    private readonly PartitionSequencer _sequencer;
    private readonly Func<EventEnvelope, IMessageContext, CancellationToken, Task> _handler;
    private readonly CancellationToken _handlerCancellationToken;
    private readonly ILogger _logger;
    private readonly string _consumerName;
    private readonly Task _dispatchLoop;

    public ConcurrentWebhookDispatchPump(
        int maxConcurrency,
        int channelCapacity,
        Func<EventEnvelope, IMessageContext, CancellationToken, Task> handler,
        ILogger logger,
        string consumerName,
        CancellationToken stoppingToken)
    {
        if (maxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency, "MaxConcurrency must be at least 1.");
        }

        _handler = handler;
        _handlerCancellationToken = stoppingToken;
        _logger = logger;
        _consumerName = consumerName;
        _sequencer = new PartitionSequencer(maxConcurrency);

        _queue = Channel.CreateBounded<QueuedDelivery>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        // Stop accepting new deliveries once the host starts stopping; the dispatch loop still
        // drains whatever is already queued, and every key's own in-flight/queued work still runs to
        // completion (concurrency.md's "drain the pipeline channel" shutdown step) - the same
        // "complete the channel, let ReadAllAsync end on its own" mechanism `4-05`'s
        // ChannelMessagePipeline uses via IHostApplicationLifetime.ApplicationStopping, adapted here
        // to the stoppingToken this pump is directly constructed with instead.
        stoppingToken.Register(() => _queue.Writer.TryComplete());

        _dispatchLoop = Task.Run(RunDispatchLoopAsync);
    }

    /// <summary>
    /// Called from the fast delegate registered with <c>IEventConsumer.SubscribeAsync</c> - the one
    /// and only writer onto <see cref="_queue"/>, matching the real production shape (a broker client
    /// invokes <c>ReceivedAsync</c> for one subscription strictly one delivery at a time; nothing else
    /// ever calls this). Wraps <paramref name="context"/> in <see cref="GatedMessageContext"/> before
    /// queueing so that every ack/nack this delivery ever triggers - whether from the real handler's
    /// own success path or this pump's own failure path (<see cref="ProcessAsync"/>) - is serialized
    /// through the same gate.
    /// </summary>
    public ValueTask EnqueueAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        var gated = new GatedMessageContext(context, _channelGate);
        return _queue.Writer.WriteAsync(new QueuedDelivery(envelope, gated), cancellationToken);
    }

    /// <summary>
    /// Awaited from each consumer's own <c>StopAsync</c> override, bounded by the same
    /// <c>ShutdownDrainTimeout</c> options pattern `4-05`'s <c>MessagePipelineWorkerHost</c> uses -
    /// concurrency.md's "drain channels" step made real for this pump too, not merely for `4-05`'s own.
    /// Waits for the dispatch loop *and* every partition's own drain to finish, not merely for the
    /// shared channel to empty - a delivery can still be mid-flight inside a key's own queue after the
    /// dispatch loop itself has already handed off its very last item.
    /// </summary>
    public async Task DrainAsync()
    {
        await _dispatchLoop;
        await _sequencer.DrainAsync();
    }

    /// <summary>
    /// The one and only reader of <see cref="_queue"/>. Hands each delivery to
    /// <see cref="PartitionSequencer.Submit"/> *synchronously*, in the exact order the broker handed
    /// them to <see cref="EnqueueAsync"/> - this single-threaded, non-awaiting handoff is what makes
    /// per-key FIFO order exact rather than merely likely (<see cref="PartitionSequencer"/>'s own
    /// remarks explain why "likely" was not good enough here). The actual (possibly slow) processing
    /// happens on whatever task <see cref="PartitionSequencer"/> schedules for that key, never on this
    /// loop itself - this loop's only job is to keep draining <see cref="_queue"/> as fast as
    /// possible, so a burst of deliveries is handed off across all their distinct keys immediately
    /// rather than one at a time.
    /// </summary>
    private async Task RunDispatchLoopAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(CancellationToken.None))
            {
                _sequencer.Submit(item.Envelope.PartitionKey, () => ProcessAsync(item));
            }
        }
        catch (Exception ex)
        {
            // concurrency.md: "an unobserved exception must not silently kill a consumer loop." This
            // loop has no BackgroundService host of its own to surface a failure through (it is a
            // plain Task.Run, matching the fact that RabbitMqEventConsumer.SubscribeAsync's own
            // returned Task already completes right after subscribing, independent of delivery
            // processing) - logging here is the only way a dead dispatch loop is ever observed,
            // instead of every future delivery on this subscription silently stalling forever.
            _logger.LogError(ex, "Concurrent webhook dispatch loop for {ConsumerName} stopped unexpectedly.", _consumerName);
        }
    }

    private async Task ProcessAsync(QueuedDelivery item)
    {
        try
        {
            await _handler(item.Envelope, item.Context, _handlerCancellationToken);
        }
        catch (Exception) when (!_handlerCancellationToken.IsCancellationRequested)
        {
            // messaging.md: handlers must be safe to run twice regardless of the inbox - a thrown
            // exception is treated exactly like an explicit NackAsync(requeue: true), the same
            // contract RabbitMqEventConsumer.SubscribeAsync's own inline processing used to provide
            // before this pump took over the "who calls Nack on failure" job.
            await item.Context.NackAsync(requeue: true, CancellationToken.None);
        }
    }

    private readonly record struct QueuedDelivery(EventEnvelope Envelope, IMessageContext Context);

    /// <summary>
    /// concurrency.md's "no lock around await" rule, applied to serializing calls onto one shared
    /// <c>IChannel</c>: a <see cref="SemaphoreSlim"/> held only across each individual ack/nack/
    /// dead-letter call, never across the handler's own (potentially slow) work.
    /// </summary>
    private sealed class GatedMessageContext(IMessageContext inner, SemaphoreSlim gate) : IMessageContext
    {
        public async Task AckAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await inner.AckAsync(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task NackAsync(bool requeue, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await inner.NackAsync(requeue, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task DeadLetterAsync(CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                await inner.DeadLetterAsync(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
    }
}

/// <summary>
/// `6-07`: a per-key FIFO mailbox with a shared, global concurrency bound - not the ref-counted
/// <c>SemaphoreSlim</c>-per-key gate `4-05`'s <c>ConversationSequencer</c> uses. That shape only
/// guarantees *mutual exclusion* (no two same-key actions run concurrently); it does not guarantee
/// *order*, because <c>SemaphoreSlim.WaitAsync</c> does not wake waiters in FIFO arrival order. `4-05`
/// never needed order preservation - its own test only asserts a gap-free ascending DB sequence, which
/// holds regardless of which of several genuinely-concurrent sends "wins." This pump's own correctness
/// requirement is strictly stronger: two deliveries for one conversation have a real, pre-existing
/// broker order (the order they were published/consumed in), and processing them out of that order is
/// exactly the bug `concurrency.md` forbids - found live by this item's own
/// <c>ConcurrentWebhookDispatchPumpTests</c> when a first draft reused `4-05`'s shape verbatim and a
/// same-key ordering assertion failed nondeterministically.
///
/// Design: each key owns a plain <c>Queue&lt;Func&lt;Task&gt;&gt;</c> plus a "someone is already
/// draining this key" flag, both protected by a per-key <c>lock</c> held only across the
/// enqueue/dequeue bookkeeping, never across an <c>await</c> (`concurrency.md`: "no lock around
/// await"). <see cref="Submit"/> is synchronous and non-blocking - it either starts a new drain loop
/// for a key that was idle, or simply appends to a key already being drained. Because the *only*
/// caller of <see cref="Submit"/> is <c>ConcurrentWebhookDispatchPump</c>'s own single dispatch loop,
/// calling it strictly in the broker's own delivery order, and because appending to a key's queue is
/// synchronous with no scheduling gap, two deliveries for the same key are appended to that key's
/// queue in exactly the order the broker delivered them - and a queue drained by exactly one active
/// loop at a time can only ever process its items in that same order. Overall concurrency *across*
/// keys is bounded by <see cref="_concurrencyGate"/>, a single <c>SemaphoreSlim(maxConcurrency)</c>
/// shared by every key's drain loop - however many distinct keys are active at once, at most
/// <c>maxConcurrency</c> of them are ever actually inside a handler call.
/// </summary>
internal sealed class PartitionSequencer(int maxConcurrency)
{
    // A plain Dictionary guarded by one coarse lock, not ConcurrentDictionary - deliberately, so the
    // "does this key still have an active mailbox" question and "create/append/retire it" answer are
    // one atomic step. 4-05's own ConversationSequencer names the exact race a per-entry lock (or a
    // bare ConcurrentDictionary's own GetOrAdd/TryRemove used independently) opens: retiring an entry
    // and a new caller's lookup can interleave so the new caller starts a *second*, independent
    // mailbox for a key that already has one, defeating the whole guarantee. One lock shared by every
    // key is cheap here regardless - the critical section is always a couple of field reads/writes,
    // never an `await` (`concurrency.md`: "no lock around await"), and the only caller of
    // <see cref="Submit"/> is already single-threaded (<c>ConcurrentWebhookDispatchPump</c>'s own
    // dispatch loop), so this lock is never meaningfully contended by producers - only by a key's own
    // drain loop retiring itself.
    private readonly Dictionary<string, KeyMailbox> _mailboxes = [];
    private readonly SemaphoreSlim _concurrencyGate = new(maxConcurrency, maxConcurrency);
    private readonly ConcurrentDictionary<Task, byte> _activeDrains = new();

    public void Submit(string partitionKey, Func<Task> action)
    {
        bool startDrain;
        KeyMailbox mailbox;
        lock (_mailboxes)
        {
            if (!_mailboxes.TryGetValue(partitionKey, out mailbox!))
            {
                mailbox = new KeyMailbox();
                _mailboxes[partitionKey] = mailbox;
            }

            mailbox.Queue.Enqueue(action);
            startDrain = !mailbox.Draining;
            mailbox.Draining = true;
        }

        if (!startDrain)
        {
            return;
        }

        var drain = Task.Run(() => DrainKeyAsync(partitionKey, mailbox));
        _activeDrains[drain] = 0;
        _ = drain.ContinueWith(
            t => _activeDrains.TryRemove(t, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Waits for every key's drain loop that was ever started to finish - used by shutdown
    /// drain (<c>ConcurrentWebhookDispatchPump.DrainAsync</c>) once the dispatch loop itself has
    /// stopped submitting new work, so no further drain can start after this snapshot is taken.
    /// </summary>
    public Task DrainAsync() => Task.WhenAll(_activeDrains.Keys.ToArray());

    private async Task DrainKeyAsync(string partitionKey, KeyMailbox mailbox)
    {
        while (true)
        {
            Func<Task> next;
            lock (_mailboxes)
            {
                if (mailbox.Queue.Count == 0)
                {
                    mailbox.Draining = false;

                    // Only retire the dictionary entry if it still points at *this* mailbox - guards
                    // against a vanishingly unlikely but real interleaving where this check and a new
                    // Submit for the same key would otherwise race outside a shared lock.
                    if (_mailboxes.TryGetValue(partitionKey, out var current) && ReferenceEquals(current, mailbox))
                    {
                        _mailboxes.Remove(partitionKey);
                    }

                    return;
                }

                next = mailbox.Queue.Dequeue();
            }

            await _concurrencyGate.WaitAsync();
            try
            {
                await next();
            }
            finally
            {
                _concurrencyGate.Release();
            }
        }
    }

    private sealed class KeyMailbox
    {
        public readonly Queue<Func<Task>> Queue = new();
        public bool Draining;
    }
}
