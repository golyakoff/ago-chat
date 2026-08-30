using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ReceiveChannelMessage;

/// <summary>
/// `14-01`: turns one external-channel message into an ordinary AGO Chat visitor message. This handler
/// is the concrete argument for `adr/0027`'s claim that AGO Inbox is not a third product: everything
/// after the identity lookup is the code path a widget message already takes, unchanged.
///
/// <para><b>Why it composes two existing handlers instead of doing the work itself.</b> This is the
/// first handler in the codebase to call another, and the alternative was considered and rejected
/// twice over. Re-implementing resolve-or-create-conversation plus enqueue here would produce a second
/// pipeline for channel messages - precisely the thing this item exists to prevent, and the kind of
/// duplication that stays correct for about one sprint. Calling <c>IMessagePipeline</c> directly
/// instead of <see cref="SendVisitorMessageHandler"/> would look tidier and would silently skip that
/// handler's per-visitor and per-site rate limits and its body validation - and an SMS flood is
/// exactly the abuse those limits exist for, so skipping them on the one path an attacker does not
/// need a browser for would be the worst possible place to lose them. The cost of composition is a
/// call graph one level deeper than a reviewer sees elsewhere here; that is why it is spelled out in
/// this comment rather than left to be discovered (clean-architecture.md's "no MediatR" note gives the
/// same reason for keeping call graphs visible).</para>
///
/// <para><b>Write order, and what a crash between the writes costs.</b> Visitor, then identity, then
/// conversation, then message - four saves, not one transaction, matching
/// <see cref="StartConversationHandler"/>'s own visitor-then-conversation precedent
/// (data-model.md: one aggregate per transaction). The order is chosen so every crash window is
/// harmless rather than confusing:
/// <list type="bullet">
/// <item>after the visitor, before the identity - leaves an orphan <c>visitors</c> row with no
/// conversation and no messages. The redelivery mints a fresh one and proceeds correctly. Invisible to
/// an operator.</item>
/// <item>after the identity - the redelivery <em>finds</em> the identity, resolves the same visitor,
/// and resumes the same conversation. Correct.</item>
/// </list>
/// The order that looks more natural - conversation first, identity last - is the one that breaks: a
/// crash before the identity save would leave the redelivery unable to recognise the sender, so it
/// would mint a second visitor and a second conversation, and the operator would see one phone number
/// as two people. The identity row is written as early as the <c>visitors</c> foreign key allows,
/// deliberately.</para>
///
/// <para><b>Ordering.</b> Nothing here reads a clock for ordering purposes, and nothing can: the only
/// <see cref="IClock"/> reads are the <see cref="ChannelIdentity"/>/<see cref="Visitor"/> timestamps,
/// and the command carries no provider timestamp at all (<see cref="ReceiveChannelMessage"/>'s own
/// remarks). Per-conversation order is assigned inside the write transaction by
/// <c>Conversation.AddVisitorMessage</c>, which is where it has always come from (CLAUDE.md rules 6
/// and 11).</para>
///
/// <para><b>Idempotency.</b> All three duplicate-creating opportunities are closed by things that
/// already existed: a redelivered message finds the existing <see cref="ChannelIdentity"/> (unique on
/// site+kind+address), so no second visitor; <see cref="StartConversationHandler"/> resumes the
/// visitor's active conversation, so no second conversation; and the derived
/// <c>ClientMessageId</c> makes <c>Conversation.AddVisitorMessage</c> return the original message
/// without burning a sequence, so no second message (CLAUDE.md rule 5).</para>
///
/// <para><b>`14-12`/`adr/0079`: one new branch, ahead of "no match -> mint a new visitor".</b> When
/// <paramref name="command"/>'s address has no existing identity, its body is first checked against a
/// live <c>PendingChannelLinkRequest</c> for this site and channel kind
/// (<see cref="TryResolveVerifiedLinkVisitorAsync"/>) - a match links this address to that request's own,
/// already-existing visitor instead of minting a new one. Every other inbound message - including one
/// from an address that already belongs to a *different* visitor, even if its body happens to equal a
/// live code by coincidence - is unaffected; see that method's own remarks for why the collision case
/// this item's backlog names needs no extra check here at all.</para>
/// </summary>
public sealed class ReceiveChannelMessageHandler(
    IChannelIdentityRepository identities,
    IVisitorRepository visitors,
    IPendingChannelLinkRequestRepository pendingLinks,
    StartConversationHandler startConversation,
    SendVisitorMessageHandler sendVisitorMessage,
    IClock clock,
    IIdGenerator idGenerator)
{
    public async Task<Result<ReceiveChannelMessageResult>> HandleAsync(
        ReceiveChannelMessage command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var identity = await identities.FindAsync(
            command.SiteId, command.Kind, command.Sender, cancellationToken);

        var visitorWasNew = false;
        if (identity is null)
        {
            // `14-12`/`adr/0079` decision 1: ahead of the ordinary "no match -> mint a new visitor"
            // path below, one new question - does this message's body exactly equal a live pending
            // link code for this site and channel kind? See TryResolveVerifiedLinkVisitorAsync's own
            // remarks for what "exactly" means and why the collision case named in this item's backlog
            // needs no extra code here at all.
            var verifiedVisitorId = await TryResolveVerifiedLinkVisitorAsync(command, now, cancellationToken);

            var visitorId = verifiedVisitorId;
            if (visitorId is null)
            {
                // A brand-new external address gets a brand-new Visitor, never a match against an
                // existing one - see ChannelIdentity's own remarks for why inference is refused here.
                visitorId = new VisitorId(idGenerator.NewId(now));
                await visitors.SaveAsync(new Visitor(visitorId.Value, command.SiteId, now), cancellationToken);
                visitorWasNew = true;
            }

            identity = ChannelIdentity.Link(
                new ChannelIdentityId(idGenerator.NewId(now)),
                command.SiteId, command.Kind, command.Sender, visitorId.Value, now);
        }
        else
        {
            identity.Touch(now);
        }

        await identities.SaveAsync(identity, cancellationToken);

        var started = await startConversation.HandleAsync(
            new StartConversation.StartConversation(command.SiteId, identity.VisitorId), cancellationToken);
        if (started.IsFailure)
        {
            return Result<ReceiveChannelMessageResult>.Failure(started.Error!.Value);
        }

        var sent = await sendVisitorMessage.HandleAsync(
            new SendVisitorMessage(
                started.Value.ConversationId,
                identity.VisitorId,
                command.Body,
                ClientMessageId: command.ExternalMessageId.ToClientMessageId(command.Kind)),
            cancellationToken);
        if (sent.IsFailure)
        {
            return Result<ReceiveChannelMessageResult>.Failure(sent.Error!.Value);
        }

        return new ReceiveChannelMessageResult(
            identity.VisitorId, started.Value.ConversationId, sent.Value, visitorWasNew);
    }

    /// <summary>
    /// `14-12`/`adr/0079` decision 1: only ever called when <paramref name="command"/>'s own address has
    /// no existing <see cref="ChannelIdentity"/> at all - the caller has already made that the
    /// precondition. "Exactly matches" is a literal equality after trimming surrounding whitespace, never
    /// a first-token command match (<see cref="LinkIdentityCommandMatcher"/>'s own, different mechanism
    /// for a different question) - a code is evidence to compare, not a command to parse.
    ///
    /// <para><b>Consumed before the new <see cref="ChannelIdentity"/> is ever created, not after.</b>
    /// The two possible crash windows are not symmetric: consuming first and then crashing before
    /// <see cref="ChannelIdentity.Link"/> burns a code and links nothing - annoying (the visitor asks for
    /// a fresh one), but never leaves a code usable a second time. The other order - link first, consume
    /// second - would mean a crash in between leaves a *still-live* code that a <em>different</em>
    /// address could later present to link a second identity to the same visitor, which is a materially
    /// worse failure than an extra retry. This mirrors this handler's own class-level remarks on why
    /// "visitor, then identity" (not the other way around) is the crash-safe order for the ordinary
    /// path.</para>
    ///
    /// <para><b>The collision case this item's backlog names - "a claimed address already linked to a
    /// different visitor is refused" - needs no code here at all.</b> This method only ever runs when
    /// <paramref name="command"/>'s own address resolved to no existing identity in the first place
    /// (the caller's precondition, restated above). If that same address instead already belongs to a
    /// *different* visitor, <c>identity</c> is non-null in the caller, this method is never called, and
    /// the ordinary <c>identity.Touch(now)</c> branch runs exactly as it always has - the message is
    /// simply delivered as that other visitor's own ordinary message. No mutation happens to either the
    /// pending request or the existing identity, which is precisely "refused, not merged": the refusal is
    /// a structural consequence of the confirmation branch's own precondition, not a second check bolted
    /// on to detect it.</para>
    /// </summary>
    private async Task<VisitorId?> TryResolveVerifiedLinkVisitorAsync(
        ReceiveChannelMessage command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidateCode = command.Body.Trim();
        if (candidateCode.Length == 0)
        {
            return null;
        }

        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(candidateCode));
        var pending = await pendingLinks.FindLiveAsync(command.SiteId, command.Kind, codeHash, now, cancellationToken);
        if (pending is null)
        {
            return null;
        }

        pending.Consume(now);
        await pendingLinks.SaveAsync(pending, cancellationToken);

        return pending.VisitorId;
    }
}
