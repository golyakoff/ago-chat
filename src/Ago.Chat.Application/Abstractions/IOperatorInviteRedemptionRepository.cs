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

    /// <summary>`25-25`: the Administrator-seat counterpart to <see cref="SeatLimitReached"/> - raised
    /// instead of it when the invite being redeemed names the seeded `"Admin"` role and the site's live
    /// count of non-removed Administrators is already at or above <see cref="Site.AdminLimit"/> at the
    /// moment this redemption's row lock was taken. The invite is left unredeemed for the identical
    /// reason <see cref="SeatLimitReached"/>'s own remarks give.</summary>
    public sealed record AdminLimitReached(int AdminLimit) : OperatorInviteRedemptionResult;

    /// <summary>`25-73`: withdrawn by the inviting site before anybody redeemed it
    /// (<see cref="OperatorInvite.Revoke"/>) - checked before any lock is taken, the identical
    /// "terminal, static fact" reasoning <see cref="NotFound"/>'s own group already gives
    /// <see cref="Expired"/>/<see cref="AlreadyRedeemed"/>.</summary>
    public sealed record Revoked : OperatorInviteRedemptionResult;

    /// <summary>`25-73`'s own real security boundary: the code named a real, live invite, but the
    /// authenticated caller's own token email does not match <see cref="OperatorInvite.Email"/> - the
    /// code says which invite, the Keycloak session says who is actually claiming it, and this item's
    /// design requires both to agree. Deliberately its own case, not folded into <see cref="NotFound"/>:
    /// unlike a code that never existed, this caller *is* signed in and the invite *is* real, so hiding
    /// that fact behind "no such invite" would only send a legitimate invitee who mistyped nothing back
    /// to try the same code again - the honest, actionable answer is "this was not sent to you".</summary>
    public sealed record EmailMismatch : OperatorInviteRedemptionResult;

    /// <summary>`25-85`: more than one live, unexpired, unrevoked invite is addressed to the same email
    /// - <see cref="IOperatorInviteRedemptionRepository.RedeemPendingForEmailAsync"/>'s own case, never
    /// reachable from <see cref="IOperatorInviteRedemptionRepository.RedeemAsync"/> (a code names exactly
    /// one invite by construction). Deliberately its own case rather than folded into
    /// <see cref="NotFound"/>: "no invite" and "more than one, and this method will not guess which"
    /// are different facts, even though both leave
    /// `Ago.Chat.Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCallerHandler`'s
    /// own caller with the same practical answer today (fall back to the manual code field the invite's
    /// own email already carries) - see that handler's own remarks for why they currently map to the
    /// same client-facing error rather than two.</summary>
    public sealed record Ambiguous : OperatorInviteRedemptionResult;

    public sealed record Success(OperatorId OperatorId, SiteId SiteId) : OperatorInviteRedemptionResult;
}

/// <summary>`23-02`: <paramref name="Name"/>/<paramref name="Email"/> are the token's own `name`/
/// `email` claims, carried through so <see cref="IOperatorInviteRedemptionRepository.RedeemAsync"/> can
/// stamp them onto the new <c>Operator</c> row it creates - optional, appended at the end rather than
/// inserted, so every existing positional construction of this record keeps compiling.
///
/// <para>`25-73`: <paramref name="Email"/> is no longer only a value to copy onto the new row - it is
/// now also compared, case-insensitively, against the invite's own <see cref="OperatorInvite.Email"/>
/// before redemption is allowed to proceed at all (<see cref="OperatorInviteRedemptionResult.EmailMismatch"/>).
/// Still nullable: a validated token that genuinely carries no `email` claim cannot agree with anything,
/// so <see langword="null"/> is treated as a mismatch rather than as "skip this check" - the same "cannot
/// agree with the invite" reading a missing value deserves everywhere else in this codebase's own
/// redemption path.</para></summary>
public sealed record RedeemOperatorInviteAttempt(
    byte[] CodeHash, string ExternalSubjectId, DateTimeOffset Now, string? Name = null, string? Email = null);

/// <summary>`25-85`: <see cref="Email"/> here plays a different role than it does in
/// <see cref="RedeemOperatorInviteAttempt"/> - there it is a second factor *checked against* a code
/// that already named one specific invite; here it is the *only* key, because this attempt carries no
/// code at all. See <see cref="IOperatorInviteRedemptionRepository.RedeemPendingForEmailAsync"/>'s own
/// remarks for why that is still the same trust boundary `25-73`'s dual check already established, not
/// a weaker one.</summary>
public sealed record RedeemPendingOperatorInviteByEmailAttempt(
    string Email, string ExternalSubjectId, DateTimeOffset Now, string? Name = null);

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

    /// <summary>`25-85`: the "activate it here" card on `OnboardingPage` (`ago-console`) needs a way to
    /// finish redemption for an invitee who never had the code in hand to begin with - see this item's
    /// own worker report for why widening <c>HasPendingOperatorInviteHandler</c> to *return* the code
    /// (the backlog's own literal suggestion) turned out to be impossible: <see cref="OperatorInvite.CodeHash"/>
    /// is a one-way SHA-256, so the plaintext code is not recoverable from storage at all, by the exact
    /// same design that makes a redeemed invite's code safe to leave in a database nobody has to trust
    /// forever.
    ///
    /// <para><b>Why this is not a weaker security boundary than <see cref="RedeemAsync"/>'s own
    /// code-plus-email check.</b> A code proves two things at once today: *which* invite (it is the only
    /// key `RedeemAsync` looks up by) and *intent* (something the invitee's own inbox handed them). For
    /// this method's own caller - already authenticated, own token email already read off the validated
    /// JWT the same way `RedeemOperatorInviteHandler`'s own email-match check already does - the "which
    /// invite" half is answered by the email lookup itself, and this item's own backlog text already
    /// names the "intent" half as no longer a gap worth guarding once the caller is genuinely
    /// authenticated: "grants exactly the trust level the already-shipped `CallbackPage` path already
    /// grants" (`CallbackPage` redeems automatically off `?inviteCode=...` the instant sign-in succeeds,
    /// with no separate click proving the person meant to redeem *this* invite over some other one they
    /// might also hold - the identical shape this method now gives the second, `/onboarding`-originated
    /// path). The one case a code's own uniqueness protects against that email alone cannot - the same
    /// address holding two or more live invites, so "redeem the one for this email" is genuinely
    /// ambiguous - is <see cref="OperatorInviteRedemptionResult.Ambiguous"/>, answered by refusing to
    /// guess rather than by picking one.</para>
    /// </summary>
    Task<OperatorInviteRedemptionResult> RedeemPendingForEmailAsync(
        RedeemPendingOperatorInviteByEmailAttempt attempt, CancellationToken cancellationToken);
}
