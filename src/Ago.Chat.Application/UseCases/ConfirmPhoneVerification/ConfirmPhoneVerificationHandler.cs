using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ConfirmPhoneVerification;

/// <summary>
/// `14-15`/`adr/0079`: the confirm half of phone verification. On
/// <see cref="PhoneVerificationConfirmOutcome.Confirmed"/>, produces a real
/// <see cref="ChannelIdentity"/> through the identical <see cref="ChannelIdentity.Link"/> static factory
/// `14-12` already built - this item's own backlog file, "Why not a parallel concept": there is exactly
/// one trust store for a channel identity, and this method's whole job is to produce the evidence that
/// store already knows how to consume, never a second one.
///
/// <para><b>Visitor-only - see <c>InitiatePhoneVerificationHandler</c>'s own remarks</b> for why this
/// item was scoped without an operator-relay entry point.</para>
///
/// <para><b>Write order on <see cref="PhoneVerificationConfirmOutcome.Confirmed"/>: the verification is
/// saved (consumed) before the <see cref="ChannelIdentity"/> is ever created or touched, not after - the
/// identical crash-safety reasoning <c>ReceiveChannelMessageHandler.TryResolveVerifiedLinkVisitorAsync</c>'s
/// own remarks give for the identical ordering choice on `14-12`'s own confirmation path.</b> A crash
/// between the two saves burns a code and links nothing - annoying (the visitor requests a fresh code),
/// but never leaves a code that is still presentable a second time. The reverse order - link first, then
/// consume - would mean a crash in between leaves a still-live code a second confirmation attempt could
/// replay, which is the materially worse failure. Two separate <c>SaveChangesAsync</c> calls, not one
/// transaction spanning both aggregates - `data-model.md`'s "one aggregate per transaction", the same
/// precedent that handler's own class-level remarks state ("four saves, not one transaction").</para>
///
/// <para><b>Reuse, not a duplicate row - this item's own Done-when, proven by a test.</b> A phone number
/// that already resolves to an <em>active</em> <see cref="ChannelIdentity"/> for <em>this same</em>
/// visitor is <see cref="ChannelIdentity.Touch"/>-ed, never re-linked - `ChannelIdentity`'s own unique
/// index on (site, kind, address) would refuse a second active row for the identical address regardless,
/// so this branch is not merely an optimisation, it is what keeps a second verification of an
/// already-verified number from throwing a unique-constraint violation instead of succeeding
/// idempotently.</para>
///
/// <para><b>A phone already verified for a <em>different</em> visitor is refused, not merged -
/// `adr/0079` decision 3, applied here for the identical reason its own "Alternatives considered"
/// section gives.</b> The submitted code was genuinely correct (this branch only runs after
/// <see cref="PhoneVerificationConfirmOutcome.Confirmed"/>), so the code is still consumed - the same
/// "burn the code, do not leave it replayable" trade-off the ordering paragraph above already accepts -
/// but no <see cref="ChannelIdentity"/> mutation happens for the colliding row, and the caller is told
/// plainly which visitor already owns the address rather than silently reattributing it
/// (<see cref="ConversationErrors.PhoneVerificationAlreadyLinkedToAnotherVisitor"/>'s own remarks). Unlike
/// `14-12`'s own inbound-message confirmation branch, this collision needs an explicit check here: that
/// handler's own collision case needs none only because its caller never reaches the confirmation branch
/// at all when the address already resolves to a different visitor's identity
/// (<c>TryResolveVerifiedLinkVisitorAsync</c>'s own remarks) - this handler's own precondition is
/// different (it loads the <see cref="PendingPhoneVerification"/> by id, not by resolving the address
/// first), so the equivalent check has to be made explicitly, here.</para>
/// </summary>
public sealed class ConfirmPhoneVerificationHandler(
    IConversationRepository conversations,
    IPendingPhoneVerificationRepository pendingVerifications,
    IChannelIdentityRepository channelIdentities,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<ConfirmedPhoneVerification>> HandleAsVisitorAsync(
        ConfirmPhoneVerificationAsVisitor command, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.VisitorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This visitor is not a participant of this conversation.");
        }

        var verification = await pendingVerifications.GetByIdAsync(command.PendingPhoneVerificationId, cancellationToken);
        if (verification is null || verification.SiteId != conversation.SiteId || verification.VisitorId != conversation.VisitorId)
        {
            // Wrong-tenant or wrong-visitor reads like no row - the same info-hiding shape every
            // cross-tenant guard in this codebase already uses (ConversationErrors.NotFound's own
            // callers).
            return ConversationErrors.PhoneVerificationNotFound(command.PendingPhoneVerificationId.Value);
        }

        var now = clock.UtcNow;
        var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(command.Code));
        var outcome = verification.AttemptConfirm(submittedHash, now);

        switch (outcome)
        {
            case PhoneVerificationConfirmOutcome.AlreadyConsumed:
                // No mutation happened (PendingPhoneVerification.AttemptConfirm's own remarks) - nothing
                // to save.
                return ConversationErrors.PhoneVerificationAlreadyConsumed();

            case PhoneVerificationConfirmOutcome.Expired:
                return ConversationErrors.PhoneVerificationExpired();

            case PhoneVerificationConfirmOutcome.LockedOut:
                // May or may not have just incremented AttemptCount on this very call (the wrong guess
                // that reached MaxAttempts) - saved unconditionally either way. Persisting an unmutated,
                // already-tracked aggregate is a harmless no-op update, and distinguishing the two
                // sub-cases here would buy nothing a caller can act on differently.
                await pendingVerifications.SaveAsync(verification, cancellationToken);
                return ConversationErrors.PhoneVerificationLockedOut();

            case PhoneVerificationConfirmOutcome.WrongCode:
                await pendingVerifications.SaveAsync(verification, cancellationToken);
                return ConversationErrors.PhoneVerificationWrongCode();

            case PhoneVerificationConfirmOutcome.Confirmed:
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unhandled phone verification confirm outcome.");
        }

        // Consumed first - see this type's own remarks on why this ordering, not the reverse, is the
        // crash-safe one.
        await pendingVerifications.SaveAsync(verification, cancellationToken);

        var address = new ExternalChannelAddress(verification.Phone);
        var existing = await channelIdentities.FindAsync(verification.SiteId, ChannelKind.Sms, address, cancellationToken);

        if (existing is not null && existing.VisitorId != verification.VisitorId)
        {
            return ConversationErrors.PhoneVerificationAlreadyLinkedToAnotherVisitor();
        }

        ChannelIdentity identity;
        bool wasNewlyLinked;
        if (existing is not null)
        {
            existing.Touch(now);
            identity = existing;
            wasNewlyLinked = false;
        }
        else
        {
            identity = ChannelIdentity.Link(
                new ChannelIdentityId(idGenerator.NewId(now)), verification.SiteId, ChannelKind.Sms, address,
                verification.VisitorId, now);
            wasNewlyLinked = true;
        }

        await channelIdentities.SaveAsync(identity, cancellationToken);

        return new ConfirmedPhoneVerification(identity.Id.Value, wasNewlyLinked);
    }
}
