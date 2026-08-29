using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RouteConversationToModule;

/// <summary>
/// `20-07`: one visitor message that might open, continue, or have nothing to do with a module task -
/// the shape mirrors `14-04`'s <c>SendOfflineAutoReply</c> exactly (a persisted trigger message's id and
/// sequence, never its body), for the identical reason: the trigger is durable
/// (<c>MessageAccepted</c>-driven) before anything looks at it, so this command carries only what the
/// event itself carries.
/// </summary>
public sealed record RouteConversationToModule(
    Guid TriggerMessageId, SiteId SiteId, ConversationId ConversationId, MessageAuthorKind TriggerAuthorKind,
    int TriggerSequence);
