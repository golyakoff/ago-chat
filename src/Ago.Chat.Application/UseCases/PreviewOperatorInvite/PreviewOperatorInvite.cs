namespace Ago.Chat.Application.UseCases.PreviewOperatorInvite;

/// <summary>
/// `23-70`: the query behind `POST /api/v1/operator-invites/preview` - the other half of this item's
/// own split (`docs/backlog/23-70-*.md`), the link half. <see cref="Code"/> is the plaintext value the
/// console read off the `/invite/{code}` browser route and resubmitted in the request body - never in
/// the request path itself (`OperatorInviteEndpoints`' own class-level remarks have the live-Jaeger
/// finding that made `POST`-with-body the answer here, not `GET`-with-path). Hashed the same way
/// <c>RedeemOperatorInviteHandler</c> already hashes it for redemption - never persisted or logged in
/// this form, matching that handler's own remarks on <see cref="Domain.OperatorInvite.CodeHash"/>.
/// </summary>
public sealed record PreviewOperatorInvite(string Code);

/// <summary>
/// The three outcomes a stranger who opens this link can be shown - "an expired or already-used
/// invitation must say so plainly, not 404 and not throw" (this item's own trap). A closed
/// <see langword="enum"/>, not a bespoke type hierarchy like <c>OperatorInviteRedemptionResult</c>:
/// unlike redemption, none of these three carries payload beyond what <see cref="OperatorInvitePreviewDto"/>
/// already does, so there is nothing a richer per-case type would buy here.
/// </summary>
public enum OperatorInvitePreviewStatus
{
    Valid,
    Expired,
    Redeemed,
}

/// <summary>The wire shape's own Application-layer source - "which shop, from whom, and that it
/// expires" (this item's own backlog text), nothing else. <see cref="InvitedByDisplayName"/> can be
/// <see langword="null"/>; see <see cref="Abstractions.OperatorInvitePreviewItem"/>'s own remarks for
/// why.</summary>
public sealed record OperatorInvitePreviewDto(
    string SiteName, string? InvitedByDisplayName, DateTimeOffset ExpiresAt, OperatorInvitePreviewStatus Status);
