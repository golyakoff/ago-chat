namespace Ago.Chat.Contracts;

/// <summary>
/// `12-02`: `GET /api/v1/owner/sites`'s response body - one keyset page of the platform owner's
/// cross-tenant overview. <paramref name="NextBefore"/> is the value to pass back as `?before=` for
/// the following page, and <see langword="null"/> once the oldest site has been reached.
/// </summary>
/// <param name="RecentWindowDays">The width, in days, of the window
/// <see cref="OwnerSiteSummaryDto.RecentMessageCount"/> and
/// <see cref="OwnerSiteSummaryDto.LastMessageAt"/> are computed over. Returned rather than left for
/// the reader to know: those two fields are meaningless without it, and a client that hardcoded an
/// assumed window would silently start lying the day the server's window changed.</param>
public sealed record OwnerSitesResponse(
    IReadOnlyList<OwnerSiteSummaryDto> Sites, Guid? NextBefore, int RecentWindowDays);

/// <summary>
/// `12-02`: one tenant as the platform owner sees it. Raw signals only - `12-02`'s Out of scope rules
/// out any computed verdict, score or flag over them ("this site looks suspicious because..."), and
/// `CLAUDE.md`'s "do not invent numbers" rules out the threshold such a verdict would need. Judging
/// what these numbers mean is a human's job; this shape's job is to be true.
/// </summary>
/// <param name="Tier">Always the literal string `"free"`.
/// <b>This is not a placeholder computation or a simplification</b> - it is the actual and only tier
/// that exists in this system today. `10-02`'s own Out of scope decided it explicitly: "no tier/plan
/// column anywhere. There is exactly one tier today (free)", and Stage 13 is where entitlements
/// become real. The field is in the response now precisely so that Stage 13 can populate it from a
/// real column without a breaking change to this contract - `api-design.md` permits adding fields
/// within a version but not renaming or removing them, so a shape that omitted `tier` today would
/// force either a breaking edit or an awkward second field later.</param>
/// <param name="CreatedAt"><see langword="null"/> for sites created before `12-02` added
/// `sites.created_at`; those rows were never backfilled because the system does not know when they
/// were created (`Ago.Chat.Domain.Site.CreatedAt`). Null means "not recorded", never "just now".</param>
/// <param name="LastMessageAt">The most recent message for this site <b>within the
/// <see cref="OwnerSitesResponse.RecentWindowDays"/> window</b>, or <see langword="null"/> when there
/// was none. A quiet-but-old tenant and a brand-new empty one are therefore indistinguishable in this
/// field - stated rather than papered over, because the alternative (an all-time maximum) is the
/// unbounded, every-partition read the windowed count exists to avoid.</param>
/// <param name="AttachmentBytes">Stored attachment bytes for this tenant, all-time, excluding deleted
/// attachments. Bytes only: `12-02` explicitly does not attach a currency or infrastructure-cost
/// figure to them, because this system holds no data from which one could be derived and inventing
/// one is forbidden.</param>
public sealed record OwnerSiteSummaryDto(
    Guid SiteId,
    string Name,
    string Tier,
    DateTimeOffset? CreatedAt,
    long SeatCount,
    long ConversationCount,
    long RecentMessageCount,
    DateTimeOffset? LastMessageAt,
    long AttachmentBytes);
