using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

/// <summary>
/// `14-14`: an operator, mid-conversation, writes down a phone number, email address, or other fact a
/// visitor just mentioned. <paramref name="ConversationId"/> is both the source of "which visitor" and
/// the tenant anchor - the identical shape <c>RequestChannelLinkFromConsole</c> already establishes for
/// itself (see <see cref="RecordVisitorContactDetailHandler"/>'s own remarks for why this reuses that
/// exact permission and lookup pattern rather than <c>ListChannelIdentitiesForVisitor</c>'s assigned-
/// operator check).
///
/// <para><paramref name="Kind"/> arrives as a raw string, not yet the validated
/// <see cref="VisitorContactDetailKind"/> - <c>RequestChannelLinkFromConsole.Kind</c>'s own precedent
/// for <see cref="ChannelKind"/>: the handler is what validates it, not the HTTP endpoint.</para>
///
/// <para>`23-09`: renamed from the bare <c>RecordVisitorContactDetail</c> to
/// <c>RecordVisitorContactDetailAsOperator</c>, beside the new <see cref="RecordVisitorContactDetailAsVisitor"/>
/// this item adds - the same "one handler, two entry points, two differently-shaped commands" split
/// <c>CreateAttachmentAsVisitor</c>/<c>CreateAttachmentAsOperator</c> already establish for
/// <c>CreateAttachmentHandler</c>.</para>
/// </summary>
public sealed record RecordVisitorContactDetailAsOperator(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, string Kind, string Value);

/// <summary>
/// `23-09`/`docs/design/decisions.md` §4: the visitor's own control, reached under
/// `AuthorizationPolicies.EitherTokenKind`'s dual-scheme policy and narrowed to a visitor caller the
/// same way <c>InitiatePhoneVerificationAsVisitor</c>'s own endpoint narrows itself
/// (<c>RecordVisitorContactDetailHandler.HandleAsVisitorAsync</c>'s own remarks). No
/// <see cref="SiteId"/> - unlike the operator command above, the visitor's token carries no RBAC
/// permission to check, so tenant scope here is entirely the participant check against
/// <paramref name="RequestedBy"/>, the identical shape <c>CreateAttachmentAsVisitor</c> already uses
/// for itself.
/// </summary>
public sealed record RecordVisitorContactDetailAsVisitor(
    ConversationId ConversationId, VisitorId RequestedBy, string Kind, string Value);
