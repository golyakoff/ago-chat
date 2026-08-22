namespace Ago.Chat.Worker;

/// <summary>Bound from <c>OperatorDisconnectSweepJob:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).
///
/// <see cref="Interval"/> is a starting point, not measured (`CLAUDE.md`) - it bounds how late the
/// sweep backstop can be for a disconnect that never fired `OperatorHub`'s own fast-path event at
/// all, so it should be shorter than `OperatorDisconnectGraceConsumerOptions.GracePeriod`, not
/// longer.</summary>
public sealed class OperatorDisconnectSweepJobOptions
{
    public const string SectionName = "OperatorDisconnectSweepJob";

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
}
