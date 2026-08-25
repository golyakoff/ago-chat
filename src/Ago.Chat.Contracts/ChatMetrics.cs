using System.Diagnostics.Metrics;

namespace Ago.Chat.Contracts;

/// <summary>
/// `7-02`: the metrics counterpart to <see cref="ChatTracing"/> - one shared <c>Meter</c> every
/// `Ago.Chat`-specific manual instrument is created against, for the same reason `ChatTracing` picked
/// this project: the lowest one every caller (hub methods in `Ago.Chat.Api`, the pipeline in
/// `Ago.Chat.Module`/`Ago.Chat.Infrastructure.Postgres`, the outbox dispatcher and assignment claim in
/// `Ago.Chat.Worker`) already reaches, without a brand-new project whose only reason to exist is one
/// <c>Meter</c> (clean-architecture.md's own premature-generalisation caution).
///
/// `Ago.Platform.Hosting.AddPlatformObservability`'s <c>"Ago.*"</c> `Meter` wildcard subscription
/// (`ServiceCollectionExtensions.MeterWildcard`) is what actually makes this Meter's instruments reach
/// an exporter - this class only needs a name starting with "Ago." to be covered.
///
/// Two instruments here (<see cref="RegisterChannelOccupancyProvider"/>'s gauge and
/// <see cref="SetOutboxLagSeconds"/>'s gauge) are deliberately *pull-based through a static slot*
/// rather than each owning class calling <c>Meter.CreateObservableGauge</c> itself in its own
/// constructor: `ChannelMessagePipeline`/`OutboxDispatcher` are ordinarily process-lifetime
/// singletons, but tests construct fresh instances repeatedly against this same static `Meter` - an
/// `ObservableGauge` registered per-instance would accumulate one registration per test, each still
/// reachable and still firing, silently reporting stale values from long-abandoned prior instances.
/// Registering the gauge exactly once, in this class's own static constructor, and letting the latest
/// instance simply overwrite where the value is read from, avoids that leak entirely without either
/// class needing to track or dispose an instrument handle.
/// </summary>
public static class ChatMetrics
{
    public const string MeterName = "Ago.Chat";

    public const string HubMethodDurationInstrumentName = "ago.chat.hub.method.duration";
    public const string HubMethodCountInstrumentName = "ago.chat.hub.method.count";
    public const string PipelineChannelOccupancyInstrumentName = "ago.chat.pipeline.channel_occupancy";
    public const string PipelineBatchSizeInstrumentName = "ago.chat.pipeline.batch_size";
    public const string PipelineEnqueueWaitInstrumentName = "ago.chat.pipeline.enqueue_wait";
    public const string OutboxLagInstrumentName = "ago.chat.outbox.lag";
    public const string OutboxPublishFailuresInstrumentName = "ago.chat.outbox.publish_failures";
    public const string AssignmentCapacityClaimAttemptsInstrumentName = "ago.chat.assignment.capacity_claim_attempts";
    public const string AssignmentCapacityClaimConflictsInstrumentName = "ago.chat.assignment.capacity_claim_conflicts";
    public const string AssignmentCapacityReleaseDeadlocksInstrumentName = "ago.chat.assignment.capacity_release_deadlocks";
    public const string DeliveryRecipientsInstrumentName = "ago.chat.delivery.recipients";

    /// <summary>The connection registry had at least one live connection for this recipient when the
    /// fan-out resolved them.</summary>
    public const string ConnectedPresence = "connected";

    /// <summary>It had none. Ordinary for a visitor who closed the tab; the reason this instrument
    /// is tagged by recipient kind at all is that the same zero means something else for an
    /// operator.</summary>
    public const string AbsentPresence = "absent";

    public static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> HubMethodDuration = Meter.CreateHistogram<double>(
        HubMethodDurationInstrumentName, unit: "s", description: "Hub method call duration, tagged by hub, method, and outcome.");

    private static readonly Counter<long> HubMethodCount = Meter.CreateCounter<long>(
        HubMethodCountInstrumentName, unit: "{call}", description: "Hub method calls, tagged by hub, method, and outcome (success/error).");

    private static readonly Histogram<int> PipelineBatchSize = Meter.CreateHistogram<int>(
        PipelineBatchSizeInstrumentName, unit: "{message}", description: "Row count of each batch MessageBatchWriter flushes.");

    private static readonly Histogram<double> PipelineEnqueueWait = Meter.CreateHistogram<double>(
        PipelineEnqueueWaitInstrumentName, unit: "s", description: "Time a message spent in ChannelMessagePipeline's bounded channel before a worker dequeued it.");

    private static readonly Counter<long> OutboxPublishFailures = Meter.CreateCounter<long>(
        OutboxPublishFailuresInstrumentName, unit: "{failure}", description: "Outbox rows that failed to publish on an attempt (timeout or exception), feeding the DLQ path once retries are exhausted downstream.");

    private static readonly Counter<long> AssignmentCapacityClaimAttempts = Meter.CreateCounter<long>(
        AssignmentCapacityClaimAttemptsInstrumentName, unit: "{attempt}", description: "IOperatorCapacity.TryClaimAsync calls, tagged by outcome (claimed/conflict).");

    private static readonly Counter<long> AssignmentCapacityClaimConflicts = Meter.CreateCounter<long>(
        AssignmentCapacityClaimConflictsInstrumentName, unit: "{conflict}", description: "IOperatorCapacity.TryClaimAsync calls that lost the race or found no capacity (a rows-affected count of 0) - concurrency.md's own 'normal outcome to retry, not an error.'");

    private static readonly Counter<long> AssignmentCapacityReleaseDeadlocks = Meter.CreateCounter<long>(
        AssignmentCapacityReleaseDeadlocksInstrumentName, unit: "{deadlock}", description: "IOperatorCapacity.ReleaseAsync calls Postgres aborted with 40P01, tagged by outcome (retried/abandoned) - `6-10`. `abandoned` is the one that matters: it means a capacity slot leaked until that operator next goes offline.");

    private static readonly Counter<long> DeliveryRecipients = Meter.CreateCounter<long>(
        DeliveryRecipientsInstrumentName,
        unit: "{recipient}",
        description:
            "Recipients a realtime fan-out resolved - one point per recipient per fan-out - tagged by the wire method "
            + "being fanned out, by recipient kind (visitor/operator), and by whether the connection registry had any "
            + "live connection for them at that moment (connected/absent). `7-08`: a raw \"delivered to zero\" count "
            + "would be noise, because fanning out to a visitor who closed the tab is the expected outcome many times "
            + "a day; what is worth looking at is an *operator* under `absent`, or a rise in `connected` recipients "
            + "whose dispatches come back `connection_not_local` on ago.platform.realtime.dispatches.");

    private static Func<int>? _channelOccupancyProvider;
    private static int _channelCapacity;
    private static double _outboxLagSeconds;

    static ChatMetrics()
    {
        Meter.CreateObservableGauge(
            PipelineChannelOccupancyInstrumentName,
            ObserveChannelOccupancy,
            unit: "{message}",
            description: "ChannelMessagePipeline's bounded inbound channel: current reader count, tagged with its configured capacity.");

        Meter.CreateObservableGauge(
            OutboxLagInstrumentName,
            () => new Measurement<double>(Volatile.Read(ref _outboxLagSeconds)),
            unit: "s",
            description: "now - the oldest unpublished outbox row's occurred_at, as of the last dispatch cycle.");
    }

    public static void RecordHubMethod(string hub, string method, TimeSpan duration, bool success)
    {
        var hubTag = new KeyValuePair<string, object?>("hub", hub);
        var methodTag = new KeyValuePair<string, object?>("method", method);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", success ? "success" : "error");
        HubMethodDuration.Record(duration.TotalSeconds, hubTag, methodTag, outcomeTag);
        HubMethodCount.Add(1, hubTag, methodTag, outcomeTag);
    }

    public static void RecordBatchSize(int size) => PipelineBatchSize.Record(size);

    public static void RecordEnqueueWait(TimeSpan wait) => PipelineEnqueueWait.Record(wait.TotalSeconds);

    public static void RecordOutboxPublishFailure() => OutboxPublishFailures.Add(1);

    public static void RecordCapacityClaimAttempt(bool claimed)
    {
        AssignmentCapacityClaimAttempts.Add(1, new KeyValuePair<string, object?>("outcome", claimed ? "claimed" : "conflict"));
        if (!claimed)
        {
            AssignmentCapacityClaimConflicts.Add(1);
        }
    }

    /// <summary>`6-10`: one counter, not two, unlike the claim pair above - there is no "attempts"
    /// denominator worth carrying here, because a release that succeeds first time is the overwhelming
    /// case and is already implied by the close count. What an alert wants is the `abandoned` tag.</summary>
    public static void RecordCapacityReleaseDeadlock(bool retried) =>
        AssignmentCapacityReleaseDeadlocks.Add(1, new KeyValuePair<string, object?>("outcome", retried ? "retried" : "abandoned"));

    /// <summary>`7-08`: one recipient of one fan-out. Called once per recipient per
    /// <c>INodeFanoutPublisher.PublishAsync</c> - see <c>Ago.Chat.Application.Realtime.
    /// FanoutObservability</c>, its only caller, for why these two tags and not a raw count.</summary>
    public static void RecordDeliveryRecipient(string method, string recipientKind, bool hadLiveConnection) =>
        DeliveryRecipients.Add(
            1,
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("recipient_kind", recipientKind),
            new KeyValuePair<string, object?>("presence", hadLiveConnection ? ConnectedPresence : AbsentPresence));

    /// <summary>Registers (overwriting any prior registration) the callback the channel-occupancy
    /// gauge reads at collection time, and the capacity it is measured against. Called from
    /// <c>ChannelMessagePipeline</c>'s own constructor - see this class's own remarks for why the
    /// gauge itself is created once, here, rather than per instance.</summary>
    public static void RegisterChannelOccupancyProvider(Func<int> readerCount, int capacity)
    {
        _channelOccupancyProvider = readerCount;
        _channelCapacity = capacity;
    }

    /// <summary>Called once per <c>OutboxDispatcher.DispatchBatchAsync</c> cycle - the gauge is only
    /// as fresh as the last dispatch cycle's own poll cadence, a deliberate, documented trade-off
    /// (this class's own remarks on why an <c>ObservableGauge</c> callback cannot itself run a DB
    /// query) rather than a live-at-scrape-time value.</summary>
    public static void SetOutboxLagSeconds(double seconds) => Volatile.Write(ref _outboxLagSeconds, seconds);

    private static IEnumerable<Measurement<int>> ObserveChannelOccupancy()
    {
        if (_channelOccupancyProvider is not { } provider)
        {
            yield break;
        }

        yield return new Measurement<int>(provider(), new KeyValuePair<string, object?>("capacity", _channelCapacity));
    }
}
