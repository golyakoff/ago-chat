using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.HandleLinkIdentityCommand;

/// <summary>
/// `14-12`/`adr/0079`: the visitor-initiated half of verified channel-identity linking - a visitor types
/// <c>/linkidentity &lt;channel-kind&gt;</c> inside a conversation they are already in, and this handler
/// creates the identical <see cref="PendingChannelLinkRequest"/> row the console-initiated path
/// (<c>RequestChannelLinkFromConsoleHandler</c>) produces, then replies in the same conversation with the
/// code and instructions.
///
/// <para><b>`MessageAccepted`-driven, the same shape `14-04`'s <c>SendOfflineAutoReplyHandler</c> and
/// `20-07`'s <c>RouteConversationToModuleHandler</c> both already establish, for the identical reason:
/// the trigger must be durable before this handler decides anything about it, and the reply travels back
/// out through the same fan-out every other message already uses.</b> A fifth consumer of the same topic,
/// alongside those two and `UnreadCounterConsumer`/`ConnectionFanoutConsumer` - two independent
/// consumers reacting to one fact is this codebase's own established shape (`ModuleTaskConsumer`'s own
/// remarks), not a reason to bolt a sixth branch onto an existing handler.</para>
///
/// <para><b>Not routed through <see cref="RouteConversationToModuleHandler"/>, and not gated on "no
/// active module task" the way that handler's own trigger-match branch is.</b> `/linkidentity` is Chat's
/// own closed, product-level vocabulary (`docs/conventions/text-commands.md`), never a per-site module
/// trigger - the two are checked independently, by construction, because a site can never register the
/// reserved word in the first place (`ReservedChatCommands`/`EnableModuleForSiteHandler`'s own
/// registration-time refusal). There is therefore no runtime precedence to get wrong between this
/// handler and <see cref="RouteConversationToModuleHandler"/>: a visitor mid-module-task who happens to
/// type <c>/linkidentity telegram</c> still gets a link request started, exactly as if no task were
/// open, because nothing about a module step's own reply-parsing (<c>ResolveReplyValue</c>) treats that
/// literal text as a valid answer to any step this codebase's own primitives define.</para>
///
/// <para><b>Idempotency (`CLAUDE.md` rule 5).</b> Everything this handler produces - the new
/// <see cref="PendingChannelLinkRequest"/> row, the reply message, its outbox row - is staged on the one
/// <c>AgoChatDbContext</c> this DI scope already shares, and <see cref="IInboxChecker.TryRecordAndSaveAsync"/>
/// performs the single <c>SaveChangesAsync</c> that commits all three together (`adr/0017`). This is why
/// <see cref="IPendingChannelLinkRequestRepository.Stage"/> exists as a separate, non-committing method
/// from <see cref="IPendingChannelLinkRequestRepository.SaveAsync"/> - see that interface's own remarks:
/// calling the committing overload here would let a redelivered trigger mint a second pending request
/// with a second code before the dedup row ever landed, the exact same mistake
/// <c>SendOfflineAutoReplyHandler</c>'s own remarks warn against for a second <c>IConversationRepository.SaveAsync</c>.</para>
/// </summary>
public sealed class HandleLinkIdentityCommandHandler(
    IConversationRepository conversations,
    IPendingChannelLinkRequestRepository pendingLinks,
    IPendingChannelLinkCodeGenerator codeGenerator,
    PendingChannelLinkRequestOptions options,
    IOutboxWriter outbox,
    IInboxChecker inbox,
    IClock clock,
    IIdGenerator idGenerator)
{
    public const string ConsumerName = "link-identity-command";

    private const string InvalidUsageText =
        "To link another channel, send: /linkidentity <channel> - for example, /linkidentity telegram.";

    public async Task<Result<LinkIdentityCommandOutcome>> HandleAsync(
        HandleLinkIdentityCommand command, CancellationToken cancellationToken)
    {
        // THE LOOP GUARD - RouteConversationToModuleHandler's and SendOfflineAutoReplyHandler's own
        // first statement, before any I/O: a reply this handler itself produces must cost this consumer
        // nothing at all. AddSystemMessage hardcodes MessageAuthorKind.System and takes no author-kind
        // parameter, so this handler's own reply can structurally never re-trigger itself.
        if (command.TriggerAuthorKind != MessageAuthorKind.Visitor)
        {
            return LinkIdentityCommandOutcome.NotAVisitorMessage;
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var trigger = conversation.Messages.FirstOrDefault(m => m.Sequence == command.TriggerSequence);
        if (trigger is null || trigger.AuthorKind != MessageAuthorKind.Visitor)
        {
            return LinkIdentityCommandOutcome.NotAVisitorMessage;
        }

        var match = LinkIdentityCommandMatcher.Match(trigger.Body.Value);
        if (match.Match == LinkIdentityCommandMatch.NotACommand)
        {
            return LinkIdentityCommandOutcome.NotThisCommand;
        }

        var now = clock.UtcNow;
        MessageBody replyBody;
        var outcome = LinkIdentityCommandOutcome.InvalidArgument;

        if (match.Match == LinkIdentityCommandMatch.InvalidArgument)
        {
            replyBody = new MessageBody(InvalidUsageText);
        }
        else
        {
            var code = codeGenerator.NewCode();
            var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            var request = PendingChannelLinkRequest.Request(
                new PendingChannelLinkRequestId(idGenerator.NewId(now)), command.SiteId, conversation.VisitorId,
                match.Kind!.Value, codeHash, requestedByOperatorId: null, now, options.ValidFor);
            // Staged, not saved - see this class's own remarks on why a second SaveChangesAsync here
            // would break the redelivery guarantee TryRecordAndSaveAsync below provides.
            pendingLinks.Stage(request);

            replyBody = new MessageBody(
                $"To link your {match.Kind} account, message us there with this code: {code}. "
                + $"It expires in {(int)options.ValidFor.TotalMinutes} minutes.");
            outcome = LinkIdentityCommandOutcome.RequestCreated;
        }

        var messageId = new MessageId(idGenerator.NewId(now));
        conversation.AddSystemMessage(messageId, replyBody, now);

        var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
        outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator));
        conversation.ClearDomainEvents();

        var isFirstDelivery = await inbox.TryRecordAndSaveAsync(command.TriggerMessageId, ConsumerName, cancellationToken);
        return isFirstDelivery ? outcome : LinkIdentityCommandOutcome.AlreadyProcessed;
    }
}
