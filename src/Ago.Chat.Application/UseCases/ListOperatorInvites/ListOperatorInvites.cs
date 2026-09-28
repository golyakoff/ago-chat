using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.ListOperatorInvites;

/// <summary>`25-73`: the query behind the console's new invite-list screen - "shown only when at least
/// one invite exists for the site" (this item's own point 7), so the console calls this on every visit
/// to `/settings/operators` and simply renders nothing extra when the list comes back empty, rather
/// than this handler taking an "only if any exist" flag of its own.</summary>
public sealed record ListOperatorInvites(OperatorId RequestedBy, SiteId SiteId);

/// <summary>`Sent`/`SendFailed`/`Revoked`/`Redeemed`/`Expired` - the five states the console's own
/// status column shows (this item's own point 7), computed here against <c>IClock</c>, never in the
/// read store's own SQL (`adr/0011`, the identical split <c>PreviewOperatorInviteHandler</c> already
/// draws for its own three-case status). Priority when more than one fact is true at once, most
/// specific/actionable first: revoked and redeemed are both permanent, terminal admin/invitee actions
/// that outrank a merely-expired clock reading; a real send failure is worth surfacing over a bare
/// "Sent" even though technically "sent" and "the relay failed" both describe the same underlying
/// `execute-actions-email` call outcome.</summary>
public enum OperatorInviteListStatus
{
    Sent,
    SendFailed,
    Revoked,
    Redeemed,
    Expired,
}

/// <summary>`26-263`: the <b>effective team-membership</b> status the «Команда → Люди» page's own status
/// badge reads - a genuinely different axis from <see cref="OperatorInviteListStatus"/> above, which is the
/// invite's *delivery* lifecycle (was it sent, did the relay fail). This one answers "where does this person
/// stand relative to the team right now", so a delivered-and-redeemed invite splits into
/// <see cref="InTeam"/> versus <see cref="Removed"/> - a distinction the delivery status collapses into a
/// single <c>Redeemed</c> and so cannot express. Additive: the delivery <see cref="OperatorInviteListStatus"/>
/// stays exactly as `25-73` shipped it (the console's existing invite-list screen still branches on it);
/// this is a second status carried alongside, never a rename of the first.
///
/// <para>Derivation priority, most terminal/specific first (computed in the handler against <c>IClock</c>,
/// `adr/0011`, never in the read store's SQL): <see cref="Revoked"/> (an admin withdrew it) outranks a
/// clock-based <see cref="Expired"/>; a redeemed invite is <see cref="InTeam"/> while its operator is still
/// active and <see cref="Removed"/> once that operator has been soft-removed; everything else - sent,
/// send-failed, still-valid, not yet redeemed - is <see cref="Pending"/>.</para></summary>
public enum OperatorInviteEffectiveStatus
{
    Pending,
    InTeam,
    Removed,
    Revoked,
    Expired,
}

/// <summary>`26-263`: <see cref="EffectiveStatus"/>, <see cref="RedeemedAt"/> and <see cref="RemovedAt"/>
/// are additive - they carry the «Команда → Люди» page's own status badge and its «Принято <c>date</c>» /
/// «Удалено <c>date</c>» lines. The pre-existing <see cref="Status"/> (delivery lifecycle) and
/// <see cref="Roles"/> (`26-258`) are unchanged. <see cref="RemovedAt"/> is the redeemed operator's own
/// <c>removed_at</c>, non-null only when <see cref="EffectiveStatus"/> is
/// <see cref="OperatorInviteEffectiveStatus.Removed"/>.</summary>
public sealed record OperatorInviteListEntry(
    Guid OperatorInviteId,
    string Email,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    OperatorInviteListStatus Status,
    string? SmtpErrorCode,
    IReadOnlyList<string> Roles,
    OperatorInviteEffectiveStatus EffectiveStatus,
    DateTimeOffset? RedeemedAt,
    DateTimeOffset? RemovedAt);
