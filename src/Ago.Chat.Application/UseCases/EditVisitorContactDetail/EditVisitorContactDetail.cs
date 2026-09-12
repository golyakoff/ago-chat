using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.EditVisitorContactDetail;

/// <summary>
/// `25-58`: an operator corrects an existing <see cref="VisitorContactDetail"/> in place - a spelling
/// fix, a corrected phone number heard mid-conversation - real inline editing, never a second,
/// competing row (the backlog item's own decision, restated in <see cref="VisitorContactDetail.EditValue"/>'s
/// own remarks). Routed through the conversation the operator is looking at, the identical shape
/// <see cref="DeleteVisitorContactDetail.DeleteVisitorContactDetail"/> already uses for itself - see
/// <see cref="EditVisitorContactDetailHandler"/>'s own remarks for why this, like that command, does not
/// take a bare id plus a site-scoped route.
///
/// <para>Operator-only, unlike <c>RecordVisitorContactDetailAsOperator</c>/<c>RecordVisitorContactDetailAsVisitor</c>'s
/// split pair - there is no visitor-facing edit, matching this item's own Scope (the visitor's own
/// widget control only ever records a fresh detail, `23-09`'s original shape, untouched by this
/// item).</para>
/// </summary>
public sealed record EditVisitorContactDetail(
    OperatorId RequestedBy, SiteId SiteId, ConversationId ConversationId, VisitorContactDetailId ContactDetailId,
    string Value);
