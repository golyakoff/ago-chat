using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ReceiveChannelAttachment;

/// <summary>
/// `25-161`: turns an inbound channel attachment into a real AGO Chat attachment on a real message -
/// the confirmed root cause and the fix for this item's second symptom
/// ("even after the grant, the image never arrives"). See this class's own two-phase split below for
/// why it is two public methods, not one, and this item's own report for the full diagnosis:
/// <c>MaxInboundMessageParser</c> had no attachment handling at all (only <c>ReceiveChannelMessage</c>'s
/// own plain-text path existed for a bot channel), so a MAX photo was either silently dropped in full
/// (no caption) or reduced to its caption alone (with one).
///
/// <para><b>Why this composes <see cref="CreateAttachmentHandler"/>/<see cref="ConfirmAttachmentHandler"/>/
/// <see cref="SendVisitorMessageHandler"/> rather than writing a fourth attachment path.</b> This
/// item's first symptom - a MAX visitor's file accepted with no `23-78` grant check at all - turned out
/// to share this exact root cause: nothing on the inbound bot-channel path ever called anything that
/// checks <see cref="Conversation.HasAttachmentUploadGrant"/>, because nothing on that path created an
/// <see cref="Attachment"/> in the first place. Routing through
/// <see cref="CreateAttachmentHandler.HandleAsVisitorAsync"/> - the exact call a widget's own presigned
/// upload already makes - means the grant check, the per-visitor/per-site rate limits and the
/// conversation/site storage budgets all apply to a MAX-sourced image identically to a widget-sourced
/// one, for free, with no second copy of any of those rules to keep in sync. This is the same "one
/// write path, however many callers reach it" reasoning
/// <see cref="Ago.Chat.Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContactHandler"/>'s
/// own remarks give for reusing
/// <see cref="Ago.Chat.Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetailHandler"/>
/// instead of writing a second contact-detail path.</para>
///
/// <para><b>Why this is two public methods (a two-phase protocol), not one <c>HandleAsync</c> the way
/// every other inbound-channel handler in this codebase is.</b> <c>Ago.Platform.Abstractions.IFileStorage</c>
/// is presign-only by design (`adr/0008`) - nothing in this codebase can hand a byte array to storage
/// from inside Application, because doing so needs a real HTTP PUT against the presigned URL
/// <see cref="CreateAttachmentHandler"/> hands back, and CLAUDE.md rule 2 forbids Application any
/// <c>HttpClient</c> of its own. <c>Ago.Chat.Worker.AttachmentThumbnailGenerator</c> is this codebase's
/// own precedent for a server-side caller uploading "exactly
/// the way a browser would" over a bare <c>HttpClient</c> - the calling adapter
/// (<c>Ago.Chat.Infrastructure.MaxBot</c>'s webhook/poller dispatch) is what plays that same role here,
/// bracketing its own PUT between this class's <see cref="PrepareAsync"/> (before: resolve the visitor,
/// check the grant, reserve budget, presign) and <see cref="CompleteAsync"/> (after: HEAD-verify and
/// post the message) - the identical split <c>CreateAttachmentHandler</c>/<c>ConfirmAttachmentHandler</c>
/// already are for a widget visitor, who plays the same "does the actual upload in between" role from
/// their own browser instead.</para>
///
/// <para><b>Identity/visitor/conversation resolution duplicates
/// <see cref="Ago.Chat.Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessageHandler"/>'s own
/// "brand-new address -&gt; mint a Visitor, then link a ChannelIdentity" branch, deliberately, rather
/// than sharing it</b> - the identical choice, and the identical reasoning,
/// <see cref="Ago.Chat.Application.UseCases.RecordChannelVisitorContact.RecordChannelVisitorContactHandler"/>'s
/// own class remarks already spell out for itself: factoring the two calls apart into a shared helper
/// would either drag this handler's own dependencies onto that carefully-reasoned class or split its
/// crash-safety argument across two files for a handful of duplicated lines.</para>
/// </summary>
public sealed class ReceiveChannelAttachmentHandler(
    IChannelIdentityRepository identities,
    IVisitorRepository visitors,
    IConversationRepository conversations,
    StartConversationHandler startConversation,
    CreateAttachmentHandler createAttachment,
    ConfirmAttachmentHandler confirmAttachment,
    SendVisitorMessageHandler sendVisitorMessage,
    GetSiteConfigByIdHandler siteConfig,
    IBillingOptionEntitlementProvider entitlements,
    IModuleQuantityGrantStore grants,
    IOutboxWriter outbox,
    IClock clock,
    IIdGenerator idGenerator,
    IVisitorEmojiPairGenerator emojiPairs)
{
    /// <summary>
    /// `25-161`: this item's own visitor-facing copy for the refused case - deliberately not
    /// <see cref="ConversationErrors.AttachmentUploadNotGranted"/>'s own message text (`"Attachments are
    /// not enabled for conversation {id}"`), which is written for an HTTP problem+json body, names an
    /// internal id, and was never meant for a person to read.
    ///
    /// <para><b>`23-78`'s own reasoning for never explaining this to an anonymous widget caller does not
    /// carry over unchanged.</b> That decision's own words: revealing the reason "would be handing a
    /// social-engineering script to exactly the flood this item exists to stop" - a real concern against
    /// `CreateAttachmentHandler`'s public, unauthenticated, infinitely-scriptable HTTP endpoint. A MAX
    /// visitor reaches this path only by first being a real party to a real conversation on MAX's own
    /// platform, subject to MAX's own account-level abuse controls - a meaningfully higher-friction
    /// surface than a bare HTTP POST, and this item's own backlog text is explicit and literal that a
    /// MAX visitor "is told they need one before a file is accepted." Both are stated here, plainly,
    /// rather than silently picking a side - see this item's own report for the same note.</para>
    /// </summary>
    private const string UploadNotGrantedVisitorMessage =
        "This conversation does not currently accept files. Please ask the operator to enable file uploads, then try again.";

    /// <summary>`25-161`: the same "no caption, use something" fallback the widget's own composer
    /// already applies (`ago-widget`'s `uploadThenSend`: `this.input.value.trim() || file.name`) - MAX
    /// gives this codebase no filename for an inbound photo, so a generic placeholder stands in for
    /// `file.name`. <see cref="MessageBody"/> rejects an empty body outright, so a caption-less
    /// photo needs <em>some</em> text regardless.</summary>
    private const string PlaceholderCaption = "Photo";

    public async Task<Result<PreparedChannelAttachmentUpload>> PrepareAsync(
        PrepareChannelAttachmentUpload command, CancellationToken cancellationToken)
    {
        // `25-170`: the identical entitlement guard `ReceiveChannelMessageHandler`'s own remarks
        // describe in full - a second, redundant check alongside the watchdog's own long-polling pause,
        // checked here (at prepare time) rather than only at complete time, since a lapsed entitlement
        // should refuse the upload before this method ever reserves budget or presigns a URL.
        if (!await ChannelEntitlement.IsEntitledAsync(entitlements, grants, command.SiteId, command.Kind, cancellationToken))
        {
            return Result<PreparedChannelAttachmentUpload>.Failure(ChannelEntitlement.Refusal(command.Kind));
        }

        var now = clock.UtcNow;

        var identity = await identities.FindAsync(command.SiteId, command.Kind, command.Sender, cancellationToken);

        VisitorId visitorId;
        if (identity is null)
        {
            // A brand-new external address gets a brand-new Visitor, never a match against an existing
            // one - see ChannelIdentity's own remarks for why inference is refused here, and this
            // class's own remarks for why this duplicates ReceiveChannelMessageHandler's identical
            // branch rather than sharing it.
            visitorId = new VisitorId(idGenerator.NewId(now));
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
            visitorId = identity.VisitorId;
        }

        await identities.SaveAsync(identity, cancellationToken);

        var started = await startConversation.HandleAsync(
            new StartConversation.StartConversation(command.SiteId, visitorId), cancellationToken);
        if (started.IsFailure)
        {
            return Result<PreparedChannelAttachmentUpload>.Failure(started.Error!.Value);
        }

        var conversationId = started.Value.ConversationId;

        // `23-78`: the one call this method makes that actually decides anything - see this class's own
        // remarks for why reusing the widget's own presign path, rather than a second grant check, is
        // the point.
        var created = await createAttachment.HandleAsVisitorAsync(
            new CreateAttachmentAsVisitor(conversationId, visitorId, command.ContentType, command.SizeBytes),
            cancellationToken);

        if (created.IsFailure)
        {
            if (created.Error!.Value.Code == "Attachment.UploadNotGranted")
            {
                await RefuseAsync(command.SiteId, conversationId, now, cancellationToken);
                return new PreparedChannelAttachmentUpload(ChannelAttachmentPrepareOutcome.Refused);
            }

            // Every other refusal (rate limited, over budget, a content type this deployment does not
            // allow) is reported the same way any other ReceiveChannelMessage failure already is - the
            // calling adapter logs it and moves on. Unlike a missing grant, none of these have a remedy
            // an ordinary system message could usefully hand the visitor (`23-78`'s own reasoning for
            // exactly this split, applied here for the identical reason).
            return Result<PreparedChannelAttachmentUpload>.Failure(created.Error!.Value);
        }

        return new PreparedChannelAttachmentUpload(
            ChannelAttachmentPrepareOutcome.Ready,
            new AttachmentId(created.Value.AttachmentId),
            visitorId,
            conversationId,
            created.Value.UploadUrl);
    }

    public async Task<Result<int>> CompleteAsync(CompleteChannelAttachmentUpload command, CancellationToken cancellationToken)
    {
        var confirmed = await confirmAttachment.HandleAsVisitorAsync(
            new ConfirmAttachmentAsVisitor(command.AttachmentId, command.VisitorId), cancellationToken);
        if (confirmed.IsFailure)
        {
            return Result<int>.Failure(confirmed.Error!.Value);
        }

        // `14-01`'s own precedent (ReceiveChannelMessageHandler composing SendVisitorMessageHandler
        // rather than IMessagePipeline directly): the rate limiting and body validation every other
        // inbound channel message already gets must not be quietly skipped for this one.
        return await sendVisitorMessage.HandleAsync(
            new SendVisitorMessage(
                command.ConversationId, command.VisitorId, PlaceholderCaption, command.AttachmentId,
                command.ExternalMessageId.ToClientMessageId(command.Kind)),
            cancellationToken);
    }

    /// <summary>
    /// `25-161`: the ungranted-file refusal, written the same way `14-04`'s offline auto-reply already
    /// is - a <see cref="Conversation.AddSystemMessage"/> plus its own outbox row, staged on the same
    /// tracked aggregate and saved together, never through the ordinary send pipeline (there is no
    /// visitor-authored message to enqueue here, only a system one replying to an attempt that never
    /// became a message at all).
    /// </summary>
    private async Task RefuseAsync(SiteId siteId, ConversationId conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetByIdAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            // Unreachable in practice: StartConversationHandler just created or resumed this exact row
            // a moment ago, inside this same request. Not worth a thrown exception over - there is
            // nothing left for this method to do either way.
            return;
        }

        var site = await siteConfig.HandleAsync(new GetSiteConfigById.GetSiteConfigById(siteId), cancellationToken);
        var retentionClass = site is null ? RetentionClass.Free : RetentionClass.FromTier(site.Tier);

        var messageId = new MessageId(idGenerator.NewId(now));
        conversation.AddSystemMessage(messageId, new MessageBody(UploadNotGrantedVisitorMessage), now, retentionClass: retentionClass);

        var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
        outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        await conversations.SaveAsync(conversation, cancellationToken);
    }
}
