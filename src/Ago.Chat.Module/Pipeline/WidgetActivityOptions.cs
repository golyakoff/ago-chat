namespace Ago.Chat.Module.Pipeline;

/// <summary>
/// Bound from <c>WidgetActivity:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). The one number here is an unmeasured starting
/// point, not a load-tested one - `CLAUDE.md`: "do not invent numbers... measure or stay silent";
/// Stage 7 is what would give it a real value, the same caveat <see cref="MessagePipelineOptions"/>'s
/// own remarks carry for its own numbers.
/// </summary>
public sealed class WidgetActivityOptions
{
    public const string SectionName = "WidgetActivity";

    /// <summary>How often <see cref="WidgetActivityFlusherService"/> drains
    /// <see cref="WidgetActivityAccumulator"/> and upserts the result. Longer than
    /// <see cref="MessagePipelineOptions.BatchMaxDelay"/> by a wide margin, deliberately: a message
    /// batch's own delay bounds how long a visitor waits to see their own message land, while this one
    /// only bounds how stale an operator's install screen is allowed to be, and `decisions.md` §3
    /// already licenses that screen being approximate.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(10);
}
