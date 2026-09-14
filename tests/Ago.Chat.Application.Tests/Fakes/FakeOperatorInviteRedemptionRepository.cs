using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// Returns a canned <see cref="OperatorInviteRedemptionResult"/> regardless of the attempt - this
/// repository's own real transactional behaviour (the `sites` row lock, the seat count, the `xmin`
/// compare-and-set) only means anything against real Postgres, so `RedeemOperatorInviteHandlerTests`
/// uses this fake purely to prove the handler's own mapping from each outcome to a `Result`/`Error`,
/// the same split `OperatorInviteRedemptionConcurrencyTests` (`Ago.Chat.Concurrency.Tests`) and the
/// production `OperatorInviteRedemptionRepository` divide the real work along.
/// </summary>
public sealed class FakeOperatorInviteRedemptionRepository(OperatorInviteRedemptionResult result)
    : IOperatorInviteRedemptionRepository
{
    public RedeemOperatorInviteAttempt? LastAttempt { get; private set; }

    /// <summary>`25-85`: the identical canned-result/last-attempt shape <see cref="LastAttempt"/>
    /// already gives <see cref="RedeemAsync"/>, for <see cref="RedeemPendingOperatorInviteForCallerHandlerTests"/>
    /// to prove its own mapping the same way <see cref="LastAttempt"/> lets the sibling tests prove
    /// theirs.</summary>
    public RedeemPendingOperatorInviteByEmailAttempt? LastPendingAttempt { get; private set; }

    public Task<OperatorInviteRedemptionResult> RedeemAsync(RedeemOperatorInviteAttempt attempt, CancellationToken cancellationToken)
    {
        LastAttempt = attempt;
        return Task.FromResult(result);
    }

    public Task<OperatorInviteRedemptionResult> RedeemPendingForEmailAsync(
        RedeemPendingOperatorInviteByEmailAttempt attempt, CancellationToken cancellationToken)
    {
        LastPendingAttempt = attempt;
        return Task.FromResult(result);
    }
}
