namespace Ago.Chat.Application.UseCases.NotifyOperatorDevices;

/// <summary>
/// Bound from <c>OperatorPushDedup:*</c> config keys, validated at startup
/// (naming-and-structure.md's options-validation rule). `26-120`: fix C (the safety net) for the
/// operator-push storm - even if some path re-triggers an operator push for the same conversation in
/// quick succession (a reconnect, a broker redelivery, a future job), the operator must not get the
/// <em>same</em> notification again within this window.
///
/// <para><b><see cref="Ttl"/> is how long a claimed marker suppresses a repeat of the same
/// notification</b> for one (operator, conversation, push-kind) tuple, the same short-TTL claim
/// `26-108`'s <c>OperatorPresenceLostSuppressionOptions</c> uses for `OperatorPresenceLost`. It only
/// ever collapses a <em>repeat</em>: the message kind keys the marker by message id as well (see
/// <c>NotifyOperatorDevicesHandler</c>'s own remarks), so a genuinely new message is a new key and
/// still pushes regardless of this value - the TTL never hides a distinct message the operator needs
/// to see.</para>
///
/// <para>Default 90s: long enough to absorb a burst of duplicate deliveries of the <em>same</em>
/// event (a redelivery, a reconnect-triggered re-fan-out) - which arrive within seconds of each
/// other - yet short enough that a genuine re-assignment or re-queue of the same conversation a
/// couple of minutes later still pushes. A starting point, not measured (CLAUDE.md: "do not invent
/// numbers... measure or stay silent"), the same caveat every sibling options class in this codebase
/// carries.</para>
/// </summary>
public sealed class OperatorPushDedupOptions
{
    public const string SectionName = "OperatorPushDedup";

    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(90);
}
