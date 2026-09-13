using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RedeemOperatorInvite;

/// <summary>
/// `13-01`: the seat-entitlement check's one real enforcement point (this item's own Goal) - a thin
/// Application-layer translation over `IOperatorInviteRedemptionRepository`'s own atomic transaction,
/// the same "handler hashes inline, then delegates the interesting work" split
/// `CreateOperatorInviteHandler` uses for generation. Everything that actually matters here - the
/// `sites` row lock, the seat count, the compare-and-set on the invite's own `redeemed_at` - lives in
/// the repository, because it is one Postgres transaction across three tables
/// (`operator_invites`/`sites`/`operators`) that no amount of Application-layer orchestration could
/// make atomic from outside a single connection.
///
/// <para><b>`25-73`: the code alone is no longer enough.</b> <c>OperatorInviteRedemptionRepository</c>
/// now also checks that <paramref name="command"/>'s own authenticated email agrees with the invite's
/// (<see cref="OperatorInviteRedemptionResult.EmailMismatch"/>) and that nobody revoked it first
/// (<see cref="OperatorInviteRedemptionResult.Revoked"/>) - this item's own real security boundary:
/// "the code says which invite, the Keycloak session says who is actually claiming it, and both must
/// agree." This handler's own job stays exactly what its class-level remarks already describe: hash,
/// delegate, map every outcome - two more arms in the same switch, not a new shape.</para>
/// </summary>
public sealed class RedeemOperatorInviteHandler(IOperatorInviteRedemptionRepository redemptions, IClock clock)
{
    public async Task<Result<RedeemedOperatorInvite>> HandleAsync(RedeemOperatorInvite command, CancellationToken cancellationToken)
    {
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(command.Code));

        var outcome = await redemptions.RedeemAsync(
            new RedeemOperatorInviteAttempt(codeHash, command.ExternalSubjectId, clock.UtcNow, command.Name, command.Email),
            cancellationToken);

        return outcome switch
        {
            OperatorInviteRedemptionResult.Success success => new RedeemedOperatorInvite(success.OperatorId, success.SiteId),
            OperatorInviteRedemptionResult.NotFound => ConversationErrors.OperatorInviteNotFound(),
            OperatorInviteRedemptionResult.Expired => ConversationErrors.OperatorInviteExpired(),
            OperatorInviteRedemptionResult.AlreadyRedeemed => ConversationErrors.OperatorInviteAlreadyRedeemed(),
            OperatorInviteRedemptionResult.AlreadyOperatorOnSite => ConversationErrors.OperatorInviteAlreadyOperatorOnSite(),
            OperatorInviteRedemptionResult.SeatLimitReached seatLimitReached =>
                ConversationErrors.OperatorInviteSeatLimitReached(seatLimitReached.SeatLimit),
            OperatorInviteRedemptionResult.AdminLimitReached adminLimitReached =>
                ConversationErrors.OperatorInviteAdminLimitReached(adminLimitReached.AdminLimit),
            OperatorInviteRedemptionResult.Revoked => ConversationErrors.OperatorInviteRevoked(),
            OperatorInviteRedemptionResult.EmailMismatch => ConversationErrors.OperatorInviteEmailMismatch(),
            _ => throw new InvalidOperationException($"Unhandled {nameof(OperatorInviteRedemptionResult)}: {outcome.GetType().Name}."),
        };
    }
}
