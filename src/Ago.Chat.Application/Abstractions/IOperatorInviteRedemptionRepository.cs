using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>What redeeming a code by its hash produces - deliberately every outcome
/// `RedeemOperatorInviteHandler` and `docs/backlog/13-01-operator-invitations-and-seat-entitlement.md`'s
/// own Done-when name, not an `enum`+nullable-payload pair. A closed hierarchy of sealed records means
/// the compiler forces every call site to handle every case (a `switch` with no default arm still warns
/// on a missing one), and <see cref="Success"/> carries exactly the two ids a caller needs rather than a
/// nullable success payload paired with a redundant status flag.</summary>
public abstract record OperatorInviteRedemptionResult
{
    private OperatorInviteRedemptionResult()
    {
    }

    /// <summary>No invite matches the presented code's hash - never redeemed, never generated, or the
    /// caller mistyped it. Answered the same whether the code truly never existed or existed and this
    /// is simply the wrong value - `RedeemOperatorInviteHandler`'s own remarks on why that is
    /// deliberate, matching `DeleteAttachmentHandler`'s info-hiding precedent for a resource belonging
    /// to someone else.</summary>
    public sealed record NotFound : OperatorInviteRedemptionResult;

    public sealed record Expired : OperatorInviteRedemptionResult;

    /// <summary>Already consumed by an earlier, successful redemption - including one this exact call
    /// lost a race to, caught by <see cref="OperatorInvite"/>'s own `xmin` optimistic-concurrency token
    /// (`OperatorInviteRedemptionRepository`'s own remarks).</summary>
    public sealed record AlreadyRedeemed : OperatorInviteRedemptionResult;

    /// <summary>`13-07`/`adr/0068`'s own adjustment to this item's originally-scoped check: the
    /// redeeming `sub` already resolves to an `Operator` row on *this invite's own* `Site` - a redundant
    /// redemption on that one site, never "resolves to an operator row anywhere" (the older, superseded
    /// rule `13-01`'s own backlog item was corrected away from once `13-07` shipped).</summary>
    public sealed record AlreadyOperatorOnSite : OperatorInviteRedemptionResult;

    /// <summary>The site's live operator count is already at or above <see cref="SeatLimit"/> at the
    /// moment this redemption's row lock was taken - the invite is deliberately left unredeemed
    /// (`OperatorInviteRedemptionRepository`'s own remarks), so a later attempt after a seat opens up
    /// succeeds against the identical code.</summary>
    public sealed record SeatLimitReached(int SeatLimit) : OperatorInviteRedemptionResult;

    public sealed record Success(OperatorId OperatorId, SiteId SiteId) : OperatorInviteRedemptionResult;
}

public sealed record RedeemOperatorInviteAttempt(byte[] CodeHash, string ExternalSubjectId, DateTimeOffset Now);

/// <summary>
/// `13-01`: the one write path that can ever add a second, third, ... `Operator` to a `Site` - the gap
/// `10-02`'s own Out of scope named and left unbuilt. Its own port, not an `OperatorInvite`
/// load-mutate-save through <see cref="IOperatorInviteRepository"/>: redemption is one step inside a
/// wider transaction that also locks the `sites` row for the seat-count check
/// (`docs/architecture/data-model.md`'s row-lock-vs-shadow-counter note - deliberately different from
/// `active_chats`' denormalized-counter pattern, because invitation is rare/low-contention where
/// `active_chats` is a high-frequency contended path) and creates the new `Operator`/`operator_roles`
/// rows, the same "its own port because it writes across more than one aggregate" reasoning
/// <see cref="ISiteRegistrationRepository"/>'s own remarks give for the bootstrap transaction it
/// mirrors.
/// </summary>
public interface IOperatorInviteRedemptionRepository
{
    Task<OperatorInviteRedemptionResult> RedeemAsync(RedeemOperatorInviteAttempt attempt, CancellationToken cancellationToken);
}
