namespace Ago.Chat.Module;

/// <summary>Bound from <c>OperatorPresenceLostSuppression:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule).
///
/// `26-108`: <see cref="Ttl"/> is how long a claimed `OperatorPresenceLost` for one operator
/// suppresses a repeat publish for that same operator, shared by every caller of
/// <see cref="OperatorPresencePublisher"/> - `Ago.Chat.Api`'s fast path
/// (`OperatorHub.OnDisconnectedAsync`) and `Ago.Chat.Worker`'s periodic backstop
/// (`OperatorDisconnectSweepJob`). It must outlive the window in which a still-disconnected,
/// still-`Assigned` operator keeps matching the sweep's own query - that window is bounded by
/// `Ago.Chat.Worker.OperatorDisconnectGraceConsumerOptions.GracePeriod` (default 30s: how long the
/// grace consumer holds a delivery before releasing), plus one more
/// `Ago.Chat.Worker.OperatorDisconnectSweepJobOptions.Interval` (default 15s) of slack for a sweep
/// tick that lands just before release completes. Default 45s = 30s + 15s.
///
/// Deliberately its own option, not a reference to either of those two: this project
/// (`Ago.Chat.Module`) sits below `Ago.Chat.Worker` in the dependency graph (hosts and the products
/// that assemble them reference `Ago.Chat.Module`, not the other way around), so it must not know
/// `Ago.Chat.Worker`'s option types exist at all. The cost of that separation is that changing
/// `GracePeriod`/`Interval` does not automatically move this value - a starting point, not measured
/// (CLAUDE.md: "do not invent numbers... measure or stay silent"), the same caveat every sibling
/// options class in this codebase already carries.</summary>
public sealed class OperatorPresenceLostSuppressionOptions
{
    public const string SectionName = "OperatorPresenceLostSuppression";

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(45);
}
