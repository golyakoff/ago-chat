using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RedeemPendingOperatorInviteForCaller;

/// <summary>
/// `25-85`: the "activate it here" card's own second redemption path, alongside the code-based one
/// <c>RedeemOperatorInviteHandler</c> already owns - built because the backlog's own literal suggestion
/// ("widen `HasPendingOperatorInviteHandler` to also return the code") turned out to be impossible:
/// <c>OperatorInvite.CodeHash</c> is a one-way SHA-256, so no handler anywhere can read a redeemable
/// code back out of storage, by the same design that makes a leaked database dump harmless to every
/// invite already sitting in it. See this item's own worker report for the full account.
///
/// <para><b>What this handler does instead, and why it is not a weaker check.</b> Rather than recover a
/// code to hand back to the console for an ordinary code-based redemption, this command redeems
/// directly, keyed by the caller's own authenticated email instead of a code -
/// <see cref="IOperatorInviteRedemptionRepository.RedeemPendingForEmailAsync"/>'s own remarks carry the
/// full security reasoning for why "authenticated, email already matches" is exactly the trust level
/// this item's own backlog names as sufficient, the same level `CallbackPage`'s existing `?inviteCode=`
/// auto-redemption already grants today.</para>
///
/// <para>Thin by design, the identical "hash, delegate, map every outcome" split
/// <c>RedeemOperatorInviteHandler</c>'s own class-level remarks describe for itself - there is no hash
/// to compute here (no code was ever presented), so this handler's only job is passing the caller's own
/// claims through and mapping the repository's outcome.</para>
/// </summary>
public sealed class RedeemPendingOperatorInviteForCallerHandler(IOperatorInviteRedemptionRepository redemptions, IClock clock)
{
    public async Task<Result<RedeemedPendingOperatorInvite>> HandleAsync(
        RedeemPendingOperatorInviteForCaller command, CancellationToken cancellationToken)
    {
        var outcome = await redemptions.RedeemPendingForEmailAsync(
            new RedeemPendingOperatorInviteByEmailAttempt(command.Email, command.ExternalSubjectId, clock.UtcNow, command.Name),
            cancellationToken);

        return outcome switch
        {
            OperatorInviteRedemptionResult.Success success => new RedeemedPendingOperatorInvite(success.OperatorId, success.SiteId),
            // `25-85`: NotFound and Ambiguous both fold into the same client-facing code - see
            // ConversationErrors.OperatorInviteNoAutoRedeemablePendingInvite's own remarks for why
            // that is the honest answer for both rather than only for one.
            OperatorInviteRedemptionResult.NotFound => ConversationErrors.OperatorInviteNoAutoRedeemablePendingInvite(),
            OperatorInviteRedemptionResult.Ambiguous => ConversationErrors.OperatorInviteNoAutoRedeemablePendingInvite(),
            OperatorInviteRedemptionResult.Expired => ConversationErrors.OperatorInviteExpired(),
            OperatorInviteRedemptionResult.AlreadyRedeemed => ConversationErrors.OperatorInviteAlreadyRedeemed(),
            OperatorInviteRedemptionResult.AlreadyOperatorOnSite => ConversationErrors.OperatorInviteAlreadyOperatorOnSite(),
            OperatorInviteRedemptionResult.SeatLimitReached seatLimitReached =>
                ConversationErrors.OperatorInviteSeatLimitReached(seatLimitReached.SeatLimit),
            OperatorInviteRedemptionResult.AdminLimitReached adminLimitReached =>
                ConversationErrors.OperatorInviteAdminLimitReached(adminLimitReached.AdminLimit),
            OperatorInviteRedemptionResult.Revoked => ConversationErrors.OperatorInviteRevoked(),
            // `25-85`: unreachable by construction - this command never presents an email that could
            // disagree with the invite it names, because the invite was found *by* that exact email.
            // Thrown, not mapped, the same "no legal recourse for a caller" shape this codebase's own
            // OperatorInviteRedemptionRepository.LockSiteAndReadCapacityAsync throw already uses for an
            // equally-unreachable contradiction.
            _ => throw new InvalidOperationException($"Unhandled {nameof(OperatorInviteRedemptionResult)}: {outcome.GetType().Name}."),
        };
    }
}
