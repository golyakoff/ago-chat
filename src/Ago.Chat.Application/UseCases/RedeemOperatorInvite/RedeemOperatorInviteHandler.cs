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
            _ => throw new InvalidOperationException($"Unhandled {nameof(OperatorInviteRedemptionResult)}: {outcome.GetType().Name}."),
        };
    }
}
