using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.HandleLinkIdentityCommand;

/// <summary>
/// `14-12`/`adr/0079`: one visitor message that might invoke Chat's own <c>/linkidentity</c> command -
/// the shape mirrors `20-07`'s <c>RouteConversationToModule</c> exactly (a persisted trigger message's
/// id and sequence, never its body), for the identical reason: the trigger is durable
/// (<c>MessageAccepted</c>-driven) before this handler looks at it.
/// </summary>
public sealed record HandleLinkIdentityCommand(
    Guid TriggerMessageId, SiteId SiteId, ConversationId ConversationId, MessageAuthorKind TriggerAuthorKind,
    int TriggerSequence);
