using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.PreviewOperatorInvite;

/// <summary>
/// `23-70`: deliberately no <see cref="IPermissionChecker"/> call anywhere in this handler - the
/// caller is, by definition, not yet an operator of anything (`IOperatorInvitePreviewReadStore`'s own
/// remarks), the identical "no permission to check yet" reasoning
/// `RedeemOperatorInviteHandler`/`SitesEndpoints.HandleRegisterSiteAsync` already establish for a
/// caller this early in the flow. What stands in for authorization here is the code itself: only
/// someone holding the high-entropy value can ask this question at all (`OperatorInviteCodeGenerator`'s
/// own 256-bit CSPRNG), and `OperatorInviteEndpoints`'s own per-IP rate limit on this route is the
/// abuse guard, the same placement `DocumentEndpoints`'s own remarks give for an anonymous read with no
/// identity to key a bucket on.
///
/// <para><b>Status is decided here, once, against <see cref="IClock"/> - never in the read store's own
/// SQL.</b> `adr/0011`: ordering and time decisions live in Application/Domain, not Infrastructure: a
/// read store returning raw <c>expires_at</c>/<c>redeemed_at</c> and letting this handler compare them
/// against "now" is the same split <see cref="Domain.OperatorInvite.IsExpired"/> already draws for the
/// redemption side of this identical row.</para>
/// </summary>
public sealed class PreviewOperatorInviteHandler(IOperatorInvitePreviewReadStore previews, IClock clock)
{
    public async Task<Result<OperatorInvitePreviewDto>> HandleAsync(PreviewOperatorInvite query, CancellationToken cancellationToken)
    {
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(query.Code));

        var row = await previews.GetByCodeHashAsync(codeHash, cancellationToken);
        if (row is null)
        {
            return ConversationErrors.OperatorInviteNotFound();
        }

        // Redeemed wins over expired when (rarely) both are true - a code redeemed a second before its
        // own expiry is "already used", not "too late", the more informative of the two true facts.
        var status = row.IsRedeemed
            ? OperatorInvitePreviewStatus.Redeemed
            : clock.UtcNow >= row.ExpiresAt
                ? OperatorInvitePreviewStatus.Expired
                : OperatorInvitePreviewStatus.Valid;

        return new OperatorInvitePreviewDto(row.SiteName, row.InvitedByDisplayName, row.ExpiresAt, status);
    }
}
