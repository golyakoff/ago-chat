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

    public Task<OperatorInviteRedemptionResult> RedeemAsync(RedeemOperatorInviteAttempt attempt, CancellationToken cancellationToken)
    {
        LastAttempt = attempt;
        return Task.FromResult(result);
    }
}
