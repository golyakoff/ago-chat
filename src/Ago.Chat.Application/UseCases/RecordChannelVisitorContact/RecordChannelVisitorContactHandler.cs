using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Application.UseCases.RecordChannelVisitorContact;

/// <summary>
/// `25-151`: turns a contact a visitor shared on a text channel into an ordinary
/// <see cref="VisitorContactDetail"/> - the one place a text-channel visitor's phone stops being thrown
/// away. Reuses <see cref="RecordVisitorContactDetailHandler.HandleAsVisitorAsync"/>'s own write path
/// rather than constructing a <see cref="VisitorContactDetail"/> directly, for the identical
/// "one write path, however many callers reach it" reasoning
/// <see cref="ReceiveChannelMessageHandler"/>'s own remarks give for composing
/// <see cref="SendVisitorMessageHandler"/> instead of re-implementing message delivery: this
/// item's own Scope text is explicit that recording a contact detail must not become a second write path
/// competing with the visitor-facing widget form, rate limits and `24-05`'s consent gate included.
///
/// <para><b>Identity/visitor/conversation resolution duplicates
/// <see cref="ReceiveChannelMessageHandler"/>'s own "brand-new address -&gt; mint a
/// Visitor, then link a ChannelIdentity" branch, deliberately, rather than sharing it.</b> That handler's
/// own class remarks spell out, in detail, why visitor-then-identity is the crash-safe write order for a
/// resolve-or-create on an inbound external address; factoring the two calls apart into a shared helper
/// both handlers call would either drag this handler's own dependencies onto that carefully-reasoned
/// class or split its crash-safety argument across two files for a handful of duplicated lines. This
/// handler does not repeat `14-12`'s pending-link-request confirmation branch at all - a contact share
/// carries no message body to compare against a live link code, so there is nothing for that branch to
/// ever match here.</para>
///
/// <para><b>Never called from inside <see cref="ReceiveChannelMessageHandler"/> itself.</b> A Telegram
/// contact message and a MAX contact attachment carry no <c>text</c> at all
/// (<c>TelegramInboundMessageParser</c>/<c>MaxInboundMessageParser</c>'s own remarks), so the calling
/// adapter (<c>TelegramLongPollingService</c>/<c>MaxLongPollingService</c>/<c>MaxWebhookEndpoints</c>)
/// dispatches to this handler directly instead of manufacturing an empty-bodied
/// <see cref="ReceiveChannelMessage"/> just to reach a conversation - an empty chat bubble nobody typed
/// would be a worse artefact than the two-handler call graph this avoids.</para>
///
/// <para><b>A rejected trust check never reaches this handler at all.</b> Both channels' own parsers
/// verify their trust rule (Telegram's <c>user_id</c> equality, MAX's HMAC hash) before this command is
/// ever constructed - by design, the same "the caller has already decided, and that decision is the one
/// with consequences" split <see cref="ChannelIdentity.Link"/>'s own remarks describe for a
/// different decision. What can still fail here is downstream of trust entirely: rate limiting and
/// `24-05`'s consent gate, both already covered because this handler calls the real write path rather
/// than a shortcut around it. Every failure is returned as an ordinary <c>Result&lt;T&gt;</c>, never
/// thrown - the calling adapter logs it and moves on, the same "a malformed inbound fact never breaks
/// the pipeline" posture <see cref="ReceiveChannelMessageHandler"/>'s own callers already hold for an
/// ordinary message.</para>
/// </summary>
public sealed class RecordChannelVisitorContactHandler(
    IChannelIdentityRepository identities,
    IVisitorRepository visitors,
    StartConversationHandler startConversation,
    RecordVisitorContactDetailHandler recordContactDetail,
    IClock clock,
    IIdGenerator idGenerator,
    IVisitorEmojiPairGenerator emojiPairs,
    ILogger<RecordChannelVisitorContactHandler> logger)
{
    public async Task<Result<RecordChannelVisitorContactResult>> HandleAsync(
        RecordChannelVisitorContact command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var identity = await identities.FindAsync(command.SiteId, command.Kind, command.Sender, cancellationToken);
        if (identity is null)
        {
            // A brand-new external address gets a brand-new Visitor, never a match against an existing
            // one - see ChannelIdentity's own remarks for why inference is refused here, and this
            // handler's own class remarks for why this is a deliberate duplicate of
            // ReceiveChannelMessageHandler's identical branch rather than a shared helper.
            var visitorId = new VisitorId(idGenerator.NewId(now));
            var newVisitor = new Visitor(visitorId, command.SiteId, now);
            var (creature, food) = emojiPairs.NextPair();
            newVisitor.AssignEmojiPair(creature, food);
            await visitors.SaveAsync(newVisitor, cancellationToken);

            identity = ChannelIdentity.Link(
                new ChannelIdentityId(idGenerator.NewId(now)),
                command.SiteId, command.Kind, command.Sender, visitorId, now);
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
            return Result<RecordChannelVisitorContactResult>.Failure(started.Error!.Value);
        }

        var phoneResult = await recordContactDetail.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(
                started.Value.ConversationId, identity.VisitorId,
                nameof(VisitorContactDetailKind.Phone), command.Phone),
            cancellationToken);
        if (phoneResult.IsFailure)
        {
            return Result<RecordChannelVisitorContactResult>.Failure(phoneResult.Error!.Value);
        }

        // The accompanying name, if the channel gave one, is best-effort: its own failure (in practice
        // only ever the same consent/rate-limit gate the phone write above already passed, revisited a
        // moment later) does not undo recording the phone, which is this item's own primary Done-when
        // promise. Logged, never thrown - the same posture this handler's own class remarks state for
        // every failure here.
        var nameRecorded = false;
        if (!string.IsNullOrWhiteSpace(command.Name))
        {
            var nameResult = await recordContactDetail.HandleAsVisitorAsync(
                new RecordVisitorContactDetailAsVisitor(
                    started.Value.ConversationId, identity.VisitorId,
                    nameof(VisitorContactDetailKind.Name), command.Name),
                cancellationToken);
            if (nameResult.IsFailure)
            {
                logger.LogWarning(
                    "Recorded a shared contact's phone for visitor {VisitorId} but could not record the " +
                    "accompanying name: {Code} {Message}",
                    identity.VisitorId.Value, nameResult.Error!.Value.Code, nameResult.Error!.Value.Message);
            }
            else
            {
                nameRecorded = true;
            }
        }

        return new RecordChannelVisitorContactResult(identity.VisitorId, started.Value.ConversationId, nameRecorded);
    }
}
