namespace Ago.Chat.Contracts;

/// <summary>
/// `25-110`: the integration event `GrantAttachmentUploadHandler`/`RevokeAttachmentUploadHandler` never
/// enqueued - the backlog item's own root cause. Carries <see cref="VisitorId"/> directly, not just
/// <see cref="ConversationId"/>: the one Worker consumer this event has
/// (`ResolveAttachmentUploadGrantDeliveryTargetsHandler`) needs to name the one live connection to reach
/// (`PrincipalKeys.ForVisitor`), and putting the visitor id on the event itself means that consumer never
/// has to load <c>Conversation</c> at all - unlike `MessageAccepted`'s own `ResolveMessageDeliveryTargetsHandler`,
/// which does need a load (it also resolves an *operator* recipient, something this event never has to do:
/// an operator who just clicked the toggle already knows the outcome from their own request's response).
///
/// <para><see cref="Granted"/> carries the *direction*, not the whole current state - `true` for a grant,
/// `false` for a revoke - matching the item's own scope ("a visitor mid-upload... when an operator revokes
/// should lose the icon live too"): a consumer forwarding this straight to the wire needs nothing more than
/// which way the toggle moved.</para>
/// </summary>
public sealed record AttachmentUploadGrantChanged(Guid ConversationId, Guid VisitorId, bool Granted, DateTimeOffset OccurredAt);
