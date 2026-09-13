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

public sealed record OperatorInviteListEntry(
    Guid OperatorInviteId,
    string Email,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    OperatorInviteListStatus Status,
    string? SmtpErrorCode);
