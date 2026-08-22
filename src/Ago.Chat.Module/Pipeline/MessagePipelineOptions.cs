namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// Bound from <c>MessagePipeline:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). Every value here is an unmeasured starting
/// point, not a load-tested number - CLAUDE.md: "do not invent numbers... measure or stay silent";
/// Stage 7 is what gives these real values.
/// </summary>
public sealed class MessagePipelineOptions
{
    public const string SectionName = "MessagePipeline";

    /// <summary>Bound on <see cref="System.Threading.Channels.Channel{T}"/>'s own capacity - past
    /// this many un-dequeued sends, a new <c>EnqueueAsync</c> call blocks (author's decision,
    /// 4-05's Scope: block with a timeout, never an unbounded queue and never a silent drop).</summary>
    public int ChannelCapacity { get; set; } = 1000;

    /// <summary>How long <c>EnqueueAsync</c> blocks against a full channel before giving up and
    /// returning a failure <c>Result</c> to the caller.</summary>
    public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How many concurrent loops <c>MessagePipelineWorkerHost</c> runs, all reading the same
    /// channel - <see cref="System.Threading.Channels.Channel{T}"/> is safe for multiple concurrent
    /// readers by design.</summary>
    public int WorkerCount { get; set; } = 4;

    /// <summary><c>BatchFlusherService</c> flushes once it has this many rows buffered, or
    /// <see cref="BatchMaxDelay"/> has passed since the batch's first row, whichever comes first.</summary>
    public int BatchMaxRows { get; set; } = 50;

    public TimeSpan BatchMaxDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Upper bound on how long <c>MessagePipelineWorkerHost.StopAsync</c> waits for the
    /// pipeline to fully drain during shutdown before giving up - the same "never block shutdown
    /// indefinitely" reasoning as <c>Ago.Platform.Realtime.DrainOptions.DrainTimeout</c>.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
